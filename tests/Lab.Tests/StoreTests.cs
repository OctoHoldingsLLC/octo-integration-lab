using Npgsql;
using Octo.IntegrationLab.Lab;
using Xunit;

namespace Octo.IntegrationLab.Tests;

[Trait("Category", "Integration")]
public sealed class StoreTests(LabFixture fixture) : IClassFixture<LabFixture>, IAsyncLifetime
{
    private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
    private NpgsqlDataSource source = null!;
    private LabStore store = null!;

    public async Task InitializeAsync()
    {
        await using var admin = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var create = admin.CreateCommand($"CREATE SCHEMA {schema}");
        await create.ExecuteNonQueryAsync();
        var config = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema };
        source = NpgsqlDataSource.Create(config.ConnectionString);
        store = new(source);
        await store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await source.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task OutboxInsertFailureRollsBackTheOrder()
    {
        await using var reject = source.CreateCommand("""
            CREATE FUNCTION reject_outbox() RETURNS trigger LANGUAGE plpgsql AS
            'BEGIN RAISE EXCEPTION ''simulated outbox failure''; END';
            CREATE TRIGGER reject_outbox BEFORE INSERT ON lab_deliveries FOR EACH ROW EXECUTE FUNCTION reject_outbox()
            """);
        await reject.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(() => store.CreateOrderAsync(Guid.NewGuid().ToString(), new("Rollback test", "LAMP-01", 1), default));
        await using var count = source.CreateCommand("SELECT count(*) FROM lab_orders");
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CompetingWorkersCannotClaimTheSameEvent()
    {
        await store.CreateOrderAsync(Guid.NewGuid().ToString(), new("Concurrent claims", "LAMP-01", 1), default);
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.ClaimAsync(15, default)));
        Assert.Single(claims, claim => claim is not null);
        await using var count = source.CreateCommand("SELECT count(*) FROM lab_attempts");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task StaleWorkerCannotOverwriteRecoveredClaim()
    {
        var order = await store.CreateOrderAsync(Guid.NewGuid().ToString(), new("Fencing test", "LAMP-01", 1), default);
        var oldClaim = (await store.ClaimAsync(15, default))!;
        await using var expire = source.CreateCommand("UPDATE lab_deliveries SET lease_until=clock_timestamp()-interval '1 second'");
        await expire.ExecuteNonQueryAsync();
        await store.RecoverExpiredAsync(4, default);
        var newClaim = (await store.ClaimAsync(15, default))!;
        Assert.NotEqual(oldClaim.Token, newClaim.Token);
        await store.CompleteAsync(oldClaim, new("Delivered", null), "Accepted", 200, 1, "stale result", default);
        Assert.Equal("Delivering", (await store.StateAsync(default)).Deliveries.Single().Status);
        await store.CompleteAsync(newClaim, new("Delivered", null), "Accepted", 200, 1, "current result", default);
        var detail = (await store.DetailAsync(order.EventId, default))!;
        Assert.Equal("Interrupted", detail.Attempts[0].Outcome);
        Assert.Equal("current result", detail.Attempts[1].Detail);
    }

    [Fact]
    public async Task AbandonedFinalAttemptExhaustsWithoutAnotherSend()
    {
        await store.CreateOrderAsync(Guid.NewGuid().ToString(), new("Final attempt", "LAMP-01", 1), default);
        await store.ClaimAsync(15, default);
        await using var expire = source.CreateCommand("UPDATE lab_deliveries SET lease_until=clock_timestamp()-interval '1 second'");
        await expire.ExecuteNonQueryAsync();
        await store.RecoverExpiredAsync(1, default);
        Assert.Null(await store.ClaimAsync(15, default));
        Assert.Equal("Exhausted", (await store.StateAsync(default)).Deliveries.Single().Status);
    }
}
