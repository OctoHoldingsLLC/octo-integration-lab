using System.Net;
using System.Net.Http.Json;
using Octo.IntegrationLab.Lab;
using Xunit;

namespace Octo.IntegrationLab.Tests;

[Trait("Category", "Integration")]
public sealed class ExperimentTests(LabFixture fixture) : IClassFixture<LabFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ConfigureAsync("healthy");
    public Task DisposeAsync() => fixture.ConfigureAsync("healthy");

    private async Task<RunCreated> CreateAsync(string scenario, int? batchSize = null, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runs");
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new ExperimentRequest(scenario, batchSize));
        using var response = await fixture.Lab.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RunCreated>())!;
    }

    private async Task<RunView> WaitAsync(Guid id, Func<RunView, bool>? predicate = null)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        RunView? last = null;
        while (DateTime.UtcNow < until)
        {
            last = await fixture.Lab.GetFromJsonAsync<RunView>($"/api/runs/{id}");
            if (last is not null && (predicate?.Invoke(last) ?? last.Complete)) return last;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Run {id}: {System.Text.Json.JsonSerializer.Serialize(last)}");
    }

    [Theory]
    [InlineData("fail-twice")]
    [InlineData("rate-limit")]
    [InlineData("permanent")]
    [InlineData("burst")]
    [InlineData("out-of-order")]
    [InlineData("two-partners")]
    [InlineData("lost-ack")]
    public async Task GuidedScenariosProveTheirOutcomes(string scenario)
    {
        var created = await CreateAsync(scenario, scenario == "burst" ? 12 : null);
        var run = await WaitAsync(created.Id);
        Assert.All(run.Steps, step => Assert.True(step.Complete));
        if (scenario == "two-partners")
        {
            Assert.Equal(2, run.Deliveries.Count);
            Assert.Single(run.Deliveries.Select(d => d.EventId).Distinct());
            Assert.Equal(1, run.Deliveries.Single(d => d.Destination == "primary").Attempts);
            Assert.Equal(3, run.Deliveries.Single(d => d.Destination == "secondary").Attempts);
            var details = await Task.WhenAll(created.DeliveryIds.Select(id => fixture.Lab.GetFromJsonAsync<DeliveryDetail>($"/api/deliveries/{id}")));
            Assert.Single(details.Select(d => d!.RawPayload).Distinct());
            Assert.All(run.Receivers.Values, events => Assert.True(Assert.Single(events!).Received));
        }
        if (scenario == "out-of-order")
        {
            var events = run.Receivers["primary"]!;
            Assert.Equal(2, events.Count);
            Assert.All(events, e => { Assert.Equal("cancelled", e.OrderStatus); Assert.Equal(2, e.OrderVersion); });
            Assert.Single(events, e => e.Effect == "Stale");
        }
        if (scenario == "permanent")
        {
            Assert.Equal("Exhausted", Assert.Single(run.Deliveries).Status);
            Assert.Equal(1, run.Deliveries[0].Attempts);
            Assert.All(run.Receivers["primary"]!, e => Assert.False(e.Received));
        }
        if (scenario is "fail-twice" or "rate-limit") Assert.Equal(3, Assert.Single(run.Deliveries).Attempts);
        if (scenario == "burst")
        {
            var metrics = (await fixture.Lab.GetFromJsonAsync<QueueMetrics>($"/api/metrics?runId={run.Run.Id}"))!;
            Assert.Equal(0, metrics.Queued);
            Assert.Equal(0, metrics.InFlight);
            Assert.Equal(12, metrics.CompletedLastMinute);
            Assert.True(metrics.AverageDeliverySeconds > 0);
        }
        if (scenario == "fail-twice")
        {
            var traced = (await fixture.Lab.GetFromJsonAsync<DeliveryDetail>($"/api/deliveries/{created.DeliveryIds[0]}"))!;
            var trace = Assert.Single(traced.Attempts.Select(a => a.TraceId).Distinct());
            Assert.Matches("^[a-f0-9]{32}$", trace!);
            Assert.All(traced.Receiver!.Observations, observation => Assert.Equal(trace, observation.TraceId));
        }
        if (scenario == "rate-limit")
        {
            var detail = (await fixture.Lab.GetFromJsonAsync<DeliveryDetail>($"/api/deliveries/{created.DeliveryIds[0]}"))!;
            Assert.True((detail.Attempts[1].StartedAt - detail.Attempts[0].CompletedAt!.Value).TotalSeconds >= 3);
            Assert.True((detail.Attempts[2].StartedAt - detail.Attempts[1].CompletedAt!.Value).TotalSeconds >= 4);
        }
    }

    [Fact]
    public async Task MaximumBurstDrainsWithBoundedConcurrentWorkers()
    {
        var created = await CreateAsync("burst", 50);
        long peak = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        QueueMetrics? metrics;
        do
        {
            metrics = await fixture.Lab.GetFromJsonAsync<QueueMetrics>($"/api/metrics?runId={created.Id}");
            peak = Math.Max(peak, metrics!.InFlight);
            Assert.InRange(metrics.InFlight, 0, 3);
            if (metrics.CompletedLastMinute == 50) break;
            await Task.Delay(75);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal(50, metrics!.CompletedLastMinute);
        Assert.InRange(peak, 2, 3);
        Assert.Equal(0, metrics.CurrentCycleRetries);
        Assert.True((await WaitAsync(created.Id)).Complete);
    }

    [Fact]
    public async Task RunRequestsAreIdempotentAndControlsAreIsolated()
    {
        var key = Guid.NewGuid().ToString();
        var copies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CreateAsync("pause", key: key)));
        Assert.Single(copies.Select(c => c.Id).Distinct());
        Assert.Single(copies, c => !c.Reused);
        var paused = copies[0];
        var failing = await CreateAsync("reject");
        await WaitAsync(failing.Id, run => run.Steps[1].Complete);
        using var recover = await fixture.Lab.PostAsJsonAsync($"/api/runs/{failing.Id}/recover", new { });
        recover.EnsureSuccessStatusCode();
        await WaitAsync(failing.Id);
        var stillPaused = (await fixture.Lab.GetFromJsonAsync<RunView>($"/api/runs/{paused.Id}"))!;
        Assert.True(stillPaused.Run.Paused);
        Assert.Equal(0, Assert.Single(stillPaused.Deliveries).Attempts);
        using var resume = await fixture.Lab.PostAsJsonAsync($"/api/runs/{paused.Id}/resume", new { });
        resume.EnsureSuccessStatusCode();
        await WaitAsync(paused.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runs");
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(new ExperimentRequest("burst", 2));
        using var conflict = await fixture.Lab.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task CircuitHoldsAttemptsThenProbesAndDrainsAfterRecovery()
    {
        var created = await CreateAsync("circuit-breaker");
        var opened = await WaitAsync(created.Id, run => run.Circuits.Any(c => c.State == "Open"));
        Assert.Contains(opened.Deliveries, d => d.Attempts == 0);
        await Task.Delay(300);
        var held = (await fixture.Lab.GetFromJsonAsync<RunView>($"/api/runs/{created.Id}"))!;
        Assert.InRange(held.Deliveries.Sum(d => d.Attempts), 2, 4);
        using var recover = await fixture.Lab.PostAsJsonAsync($"/api/runs/{created.Id}/recover", new { });
        recover.EnsureSuccessStatusCode();
        var completed = await WaitAsync(created.Id);
        Assert.Equal("Closed", Assert.Single(completed.Circuits).State);
        Assert.Contains(completed.Circuits[0].History, e => e.State == "HalfOpen");
        Assert.All(completed.Deliveries, d => Assert.InRange(d.Attempts, 1, 2));
    }
}
