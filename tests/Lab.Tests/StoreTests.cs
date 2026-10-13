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
    public async Task MigrationsAdoptExistingDataAndRejectEditedHistory()
    {
        var order = await store.CreateOrderAsync(Guid.NewGuid().ToString(), new("Migration survivor", "KIT-03", 1), default);
        var before = (await store.DetailAsync(order.EventId, default))!.RawPayload;
        await store.InitializeAsync();
        await store.InitializeAsync();
        Assert.Equal(before, (await store.DetailAsync(order.EventId, default))!.RawPayload);
        await using var corrupt = source.CreateCommand("UPDATE schema_versions SET checksum='modified' WHERE owner='lab'");
        await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync());
    }

    [Fact]
    public async Task FirstReleaseDatabaseMigratesWithoutChangingEventBytes()
    {
        var legacySchema = "legacy_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {legacySchema}")) await create.ExecuteNonQueryAsync();
        try
        {
            var configuration = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = legacySchema };
            await using var legacy = NpgsqlDataSource.Create(configuration.ConnectionString);
            await using var stream = typeof(LabStore).Assembly.GetManifestResourceStream("Lab.schema.sql")!;
            using var reader = new StreamReader(stream);
            await using (var baseline = legacy.CreateCommand(await reader.ReadToEndAsync())) await baseline.ExecuteNonQueryAsync();
            var id = Guid.NewGuid();
            const string payload = "{ \"legacy\": true }";
            await using (var order = legacy.CreateCommand("INSERT INTO lab_orders(id,request_key,request_hash,reference,sku,quantity) VALUES($1,'legacy-key','hash','Legacy','LAMP-01',1)"))
            {
                order.Parameters.AddWithValue(id);
                await order.ExecuteNonQueryAsync();
            }
            await using (var delivery = legacy.CreateCommand("INSERT INTO lab_deliveries(id,order_id,payload) VALUES($1,$1,$2)"))
            {
                delivery.Parameters.AddWithValue(id);
                delivery.Parameters.AddWithValue(payload);
                await delivery.ExecuteNonQueryAsync();
            }
            var migrated = new LabStore(legacy);
            await migrated.InitializeAsync();
            await migrated.InitializeAsync();
            var detail = (await migrated.DetailAsync(id, default))!;
            Assert.Equal(payload, detail.RawPayload);
            Assert.Equal(id, detail.Delivery.EventId);
            Assert.Equal("primary", detail.Delivery.Destination);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {legacySchema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SearchPaginationAndDetailKeepOldEventsInspectable()
    {
        var first = await store.CreateOrderAsync(Guid.NewGuid().ToString(), new("Find this old order", "KIT-03", 1), default);
        for (var i = 0; i < 105; i++)
            await store.CreateOrderAsync(Guid.NewGuid().ToString(), new($"Newer {i}", "LAMP-01", 1), default);
        var page = await store.StateAsync(default, page: 2, pageSize: 20);
        Assert.Equal(106, page.Matched);
        Assert.Equal(20, page.Deliveries.Count);
        var found = await store.StateAsync(default, "find this", "Pending");
        Assert.Equal(first.EventId, Assert.Single(found.Deliveries).Id);
        var detail = (await store.DetailAsync(first.EventId, default))!;
        Assert.Equal("Find this old order", detail.Delivery.Reference);
        Assert.Equal(first.EventId, detail.Payload.GetProperty("id").GetGuid());
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(detail.RawPayload)));
        Assert.Equal(expected, detail.PayloadHash);
        Assert.Empty((await store.StateAsync(default, "find this", "Delivered")).Deliveries);
    }

    [Fact]
    public async Task CircuitAllowsOneProbeAndRecoversAnAbandonedProbeWithFencing()
    {
        var run = await store.CreateRunAsync(Guid.NewGuid().ToString(), new("circuit-breaker"), default);
        for (var i = 0; i < 2; i++)
        {
            var claim = (await store.ClaimAsync(15, default))!;
            await store.CompleteAsync(claim, new("Retrying", TimeSpan.FromSeconds(2)), "Failed", 503, 1, "Outage", default);
        }
        Assert.Null(await store.ClaimAsync(15, default));
        Assert.Equal(2, (await store.StateAsync(default, runId: run.Id)).Deliveries.Sum(d => d.Attempts));
        await using (var advance = source.CreateCommand("UPDATE lab_circuits SET open_until=clock_timestamp()-interval '1 second'"))
            await advance.ExecuteNonQueryAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ClaimAsync(15, default)));
        var oldProbe = Assert.Single(claims, c => c is not null)!;
        await using (var expire = source.CreateCommand("UPDATE lab_deliveries SET lease_until=clock_timestamp()-interval '1 second' WHERE status='Delivering'; UPDATE lab_circuits SET probe_lease_until=clock_timestamp()-interval '1 second'"))
            await expire.ExecuteNonQueryAsync();
        await store.RecoverExpiredAsync(4, default);
        var newProbe = (await store.ClaimAsync(15, default))!;
        await store.CompleteAsync(oldProbe, new("Delivered", null), "Accepted", 200, 1, "Stale probe", default);
        Assert.Equal("HalfOpen", Assert.Single(await store.CircuitsAsync(run.Id, default)).State);
        await store.CompleteAsync(newProbe, new("Delivered", null), "Accepted", 200, 1, "Current probe", default);
        Assert.Equal("Closed", Assert.Single(await store.CircuitsAsync(run.Id, default)).State);
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
    public async Task QueueMetricsTrackAgeScopeRetriesCompletionsAndReplay()
    {
        var run = await store.CreateRunAsync(Guid.NewGuid().ToString(), new("burst", 3), default);
        await store.CreateOrderAsync(Guid.NewGuid().ToString(), new("Outside run", "KIT-03", 1), default);
        await using (var age = source.CreateCommand("UPDATE lab_deliveries SET created_at=clock_timestamp()-interval '10 seconds'"))
            await age.ExecuteNonQueryAsync();
        var queued = await store.MetricsAsync(run.Id, default);
        Assert.Equal(3, queued.Queued);
        Assert.Equal(0, queued.InFlight);
        Assert.True(queued.OldestQueuedSeconds >= 10);
        Assert.Null(queued.AverageDeliverySeconds);
        Assert.Equal(4, (await store.MetricsAsync(null, default)).Queued);
        var claim = (await store.ClaimAsync(15, default))!;
        Assert.Equal(run.Id, claim.RunId);
        Assert.Equal(1, (await store.MetricsAsync(run.Id, default)).InFlight);
        await store.CompleteAsync(claim, new("Retrying", TimeSpan.Zero), "Failed", 503, 1, "Retry", default);
        // Complete the other queued deliveries so this claim can retry in FIFO order.
        for (var i = 0; i < 3; i++)
        {
            var other = (await store.ClaimAsync(15, default))!;
            await store.CompleteAsync(other, new("Delivered", null), "Accepted", 200, 1, "Done", default);
        }
        var retry = (await store.ClaimAsync(15, default))!;
        Assert.Equal(claim.Id, retry.Id);
        await store.CompleteAsync(retry, new("Delivered", null), "Accepted", 200, 1, "Done", default);
        var drained = await store.MetricsAsync(run.Id, default);
        Assert.Equal(0, drained.Queued);
        Assert.Null(drained.OldestQueuedSeconds);
        Assert.Equal(1, drained.CurrentCycleRetries);
        Assert.Equal(3, drained.CompletedLastMinute);
        Assert.True(drained.AverageDeliverySeconds >= 10);
        Assert.True(drained.P95DeliverySeconds >= drained.AverageDeliverySeconds);
        await store.ReplayAsync(claim.Id, default);
        Assert.Equal(0, (await store.MetricsAsync(run.Id, default)).CurrentCycleRetries);
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
        await store.RecordTraceAsync(newClaim, "current-trace", default);
        await store.RecordTraceAsync(oldClaim, "stale-trace", default);
        await store.CompleteAsync(oldClaim, new("Delivered", null), "Accepted", 200, 1, "stale result", default);
        Assert.Equal("Delivering", (await store.StateAsync(default)).Deliveries.Single().Status);
        await store.CompleteAsync(newClaim, new("Delivered", null), "Accepted", 200, 1, "current result", default);
        var detail = (await store.DetailAsync(order.EventId, default))!;
        Assert.Equal("Interrupted", detail.Attempts[0].Outcome);
        Assert.Equal("current result", detail.Attempts[1].Detail);
        Assert.Equal("current-trace", detail.Attempts[1].TraceId);
        Assert.Null(detail.Attempts[0].TraceId);
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
