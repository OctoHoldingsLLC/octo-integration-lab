using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Octo.IntegrationLab.Tests;

[Trait("Category", "Integration")]
public sealed class WorkflowTests(LabFixture fixture) : IClassFixture<LabFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ConfigureAsync("healthy");
    public Task DisposeAsync() => fixture.ConfigureAsync("healthy");

    [Fact]
    public async Task OrderAndOutboxAreAtomicAndConcurrentRetriesReturnSameOrder()
    {
        await fixture.ConfigureAsync("healthy", paused: true);
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.OrderAsync(key)));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            var values = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
            var id = values[0].GetProperty("eventId").GetGuid();
            Assert.All(values, v => Assert.Equal(id, v.GetProperty("eventId").GetGuid()));
            Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM lab_orders o JOIN lab_deliveries d ON d.order_id=o.id WHERE d.id=$1", id));
            Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM lab_attempts WHERE delivery_id=$1", id));
            using var conflict = await fixture.OrderAsync(key, new("different", "KIT-03", 1));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            await fixture.ConfigureAsync("healthy");
            await fixture.WaitAsync(id, "Delivered");
            Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM partner_orders WHERE event_id=$1", id));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task HttpFailureRetriesThenRecovers()
    {
        await fixture.ConfigureAsync("reject");
        var id = await fixture.CreateAsync();
        await fixture.WaitAsync(id, "Retrying");
        await fixture.ConfigureAsync("healthy");
        var delivered = await fixture.WaitAsync(id, "Delivered");
        Assert.True(delivered.GetProperty("attempts").GetInt32() >= 2);
        var attempts = (await fixture.DetailAsync(id)).GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(503, attempts[0].GetProperty("httpStatus").GetInt32());
        Assert.Equal("Accepted", attempts[^1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task RetryBudgetIsBoundedAndReplayPreservesHistoryAndEventId()
    {
        await fixture.ConfigureAsync("reject");
        var id = await fixture.CreateAsync();
        var exhausted = await fixture.WaitAsync(id, "Exhausted");
        Assert.Equal(4, exhausted.GetProperty("attempts").GetInt32());
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM partner_orders WHERE event_id=$1", id));
        await fixture.ConfigureAsync("healthy");
        using var replay = await fixture.Lab.PostAsJsonAsync($"/api/deliveries/{id}/replay", new { });
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var delivered = await fixture.WaitAsync(id, "Delivered");
        Assert.Equal(5, delivered.GetProperty("attempts").GetInt32());
        Assert.Equal(1, delivered.GetProperty("replays").GetInt32());
        Assert.Equal(5, (await fixture.DetailAsync(id)).GetProperty("attempts").GetArrayLength());
    }

    [Fact]
    public async Task LostAcknowledgementAndManualReplayDoNotDuplicateBusinessEffect()
    {
        await fixture.ConfigureAsync("lost-ack");
        var id = await fixture.CreateAsync();
        await fixture.WaitAsync(id, "Delivered");
        var detail = await fixture.DetailAsync(id);
        var attempts = detail.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal("Timeout", attempts[0].GetProperty("outcome").GetString());
        Assert.Equal("Duplicate", attempts[^1].GetProperty("outcome").GetString());
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM partner_orders WHERE event_id=$1", id));
        using var replay = await fixture.Lab.PostAsJsonAsync($"/api/deliveries/{id}/replay", new { });
        replay.EnsureSuccessStatusCode();
        await fixture.WaitAsync(id, "Delivered");
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM partner_orders WHERE event_id=$1", id));
        Assert.Equal(2, await fixture.CountAsync("SELECT duplicates::bigint FROM partner_receipts WHERE event_id=$1", id));
    }

    [Theory]
    [InlineData("slow", "Timeout")]
    [InlineData("offline", "NetworkError")]
    public async Task SlowOrDisconnectedReceiverRecovers(string mode, string outcome)
    {
        await fixture.ConfigureAsync(mode);
        var id = await fixture.CreateAsync();
        await fixture.WaitAsync(id, "Retrying");
        await fixture.ConfigureAsync("healthy");
        await fixture.WaitAsync(id, "Delivered");
        var detail = await fixture.DetailAsync(id);
        Assert.Equal(outcome, detail.GetProperty("attempts")[0].GetProperty("outcome").GetString());
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM partner_orders WHERE event_id=$1", id));
    }

    [Fact]
    public async Task ExpiredLeaseIsRecoveredAndItsHistoryExplainsUncertainty()
    {
        await fixture.ConfigureAsync("healthy", paused: true);
        var id = await fixture.CreateAsync();
        await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var command = source.CreateCommand("""
            WITH claimed AS (
                UPDATE lab_deliveries SET status='Delivering', attempts=1, cycle_attempts=1,
                    claim_token=$2, lease_until=clock_timestamp()-interval '1 second' WHERE id=$1 RETURNING id
            )
            INSERT INTO lab_attempts (id, delivery_id, number) SELECT $3,id,1 FROM claimed
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
        await fixture.ConfigureAsync("healthy");
        await fixture.WaitAsync(id, "Delivered");
        var detail = await fixture.DetailAsync(id);
        Assert.Equal("Interrupted", detail.GetProperty("attempts")[0].GetProperty("outcome").GetString());
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM partner_orders WHERE event_id=$1", id));
    }

    [Fact]
    public async Task PostRequiresJsonAndSameOriginAndValidOrder()
    {
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/worker");
        crossSite.Headers.Add("Origin", "https://unrelated.example");
        crossSite.Content = JsonContent.Create(new { paused = true });
        using var forbidden = await fixture.Lab.SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var invalid = await fixture.OrderAsync(Guid.NewGuid().ToString(), new("", "INVALID", -1));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missingKey = await fixture.Lab.PostAsJsonAsync("/api/orders", new OrderRequest("missing", "LAMP-01", 1));
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using var wrongType = await fixture.Lab.PostAsync("/api/worker", new StringContent("{\"paused\":true}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
    }
}
