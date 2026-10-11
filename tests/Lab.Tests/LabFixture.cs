using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Octo.IntegrationLab.Tests;

public sealed class LabFixture : IAsyncLifetime
{
    public HttpClient Lab { get; } = new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("LAB_BASE_URL") ?? "http://localhost:5188") };
    public HttpClient Partner { get; } = new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("PARTNER_BASE_URL") ?? "http://localhost:5189") };
    public string Secret { get; } = Environment.GetEnvironmentVariable("SigningSecret") ?? "local-lab-signing-secret-change-outside-demo";
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("ConnectionStrings__Lab")
        ?? "Host=localhost;Port=55432;Database=integration_lab;Username=lab;Password=local-lab-password";

    public async Task InitializeAsync()
    {
        using var health = await Lab.GetAsync("/health");
        health.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        Lab.Dispose();
        Partner.Dispose();
        return Task.CompletedTask;
    }

    public async Task ConfigureAsync(string mode, bool paused = false)
    {
        using var worker = await Lab.PostAsJsonAsync("/api/worker", new { paused });
        worker.EnsureSuccessStatusCode();
        using var partner = await Lab.PostAsJsonAsync("/api/partner", new PartnerControl(mode));
        partner.EnsureSuccessStatusCode();
    }

    public async Task<HttpResponseMessage> OrderAsync(string key, OrderRequest? order = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(order ?? new OrderRequest("Test-" + key[..8], "LAMP-01", 2));
        return await Lab.SendAsync(request);
    }

    public async Task<Guid> CreateAsync()
    {
        using var response = await OrderAsync(Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();
    }

    public async Task<JsonElement> WaitAsync(Guid id, string status, int seconds = 40)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        string last = "missing";
        while (DateTime.UtcNow < deadline)
        {
            var state = await Lab.GetFromJsonAsync<JsonElement>("/api/state");
            foreach (var delivery in state.GetProperty("deliveries").EnumerateArray())
            {
                if (delivery.GetProperty("id").GetGuid() != id) continue;
                last = delivery.GetProperty("status").GetString()!;
                if (last == status) return delivery.Clone();
            }
            await Task.Delay(150);
        }
        throw new TimeoutException($"Expected event {id} to become {status}; last status was {last}.");
    }

    public async Task<JsonElement> DetailAsync(Guid id) => await Lab.GetFromJsonAsync<JsonElement>($"/api/deliveries/{id}");

    public async Task<long> CountAsync(string sql, Guid id)
    {
        await using var source = NpgsqlDataSource.Create(ConnectionString);
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
