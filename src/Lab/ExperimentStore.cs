using Npgsql;

namespace Octo.IntegrationLab.Lab;

public sealed record RunCreated(Guid Id, bool Reused, bool Conflict, List<Guid> DeliveryIds);
public sealed record RunSummary(Guid Id, string Scenario, int BatchSize, bool Paused, DateTimeOffset CreatedAt);

public sealed partial class LabStore
{
    public async Task<List<AttemptView>> RunAttemptsAsync(Guid id, CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            SELECT a.number,a.started_at,a.completed_at,a.outcome,a.http_status,a.duration_ms,a.detail
            FROM lab_attempts a JOIN lab_deliveries d ON d.id=a.delivery_id JOIN lab_events e ON e.id=d.event_id
            WHERE e.run_id=$1 ORDER BY a.started_at
            """);
        command.Parameters.AddWithValue(id);
        await using var row = await command.ExecuteReaderAsync(token);
        List<AttemptView> attempts = [];
        while (await row.ReadAsync(token)) attempts.Add(new(row.GetInt32(0), row.GetFieldValue<DateTimeOffset>(1),
            row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2), row.GetString(3),
            row.IsDBNull(4) ? null : row.GetInt32(4), row.IsDBNull(5) ? null : row.GetInt32(5), row.IsDBNull(6) ? null : row.GetString(6)));
        return attempts;
    }

    public async Task<RunCreated> CreateRunAsync(string key, ExperimentRequest request, CancellationToken token)
    {
        var definition = Experiments.Find(request.Scenario) ?? throw new ArgumentException("Unknown scenario.");
        var count = request.BatchSize ?? definition.Orders;
        var id = Guid.NewGuid();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO lab_runs(id,request_key,scenario,batch_size,paused) VALUES($1,$2,$3,$4,$5)
            ON CONFLICT(request_key) DO NOTHING RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(key);
        insert.Parameters.AddWithValue(request.Scenario);
        insert.Parameters.AddWithValue(count);
        insert.Parameters.AddWithValue(request.Scenario == "pause");
        if (await insert.ExecuteScalarAsync(token) is null)
        {
            await using var existing = new NpgsqlCommand("SELECT id,scenario,batch_size FROM lab_runs WHERE request_key=$1", connection, transaction);
            existing.Parameters.AddWithValue(key);
            bool conflict;
            await using (var row = await existing.ExecuteReaderAsync(token))
            {
                await row.ReadAsync(token);
                id = row.GetGuid(0);
                conflict = row.GetString(1) != request.Scenario || row.GetInt32(2) != count;
            }
            return new(id, true, conflict, await RunDeliveryIdsAsync(connection, transaction, id, token));
        }
        if (request.Scenario == "circuit-breaker")
        {
            await using var circuit = new NpgsqlCommand("INSERT INTO lab_circuits(run_id,destination) VALUES($1,'primary')", connection, transaction);
            circuit.Parameters.AddWithValue(id);
            await circuit.ExecuteNonQueryAsync(token);
        }
        List<Guid> ids = [];
        for (var i = 0; i < count; i++)
        {
            var orderId = Guid.NewGuid();
            var order = new OrderRequest($"{request.Scenario.ToUpperInvariant()}-{id.ToString()[..8]}-{i + 1:00}", "LAMP-01", 1);
            await using var save = new NpgsqlCommand("""
                INSERT INTO lab_orders(id,request_key,request_hash,reference,sku,quantity) VALUES($1,$2,$3,$4,$5,$6)
                """, connection, transaction);
            save.Parameters.AddWithValue(orderId);
            save.Parameters.AddWithValue($"run:{id}:{i}");
            save.Parameters.AddWithValue(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(order, LabJson.Options)))));
            save.Parameters.AddWithValue(order.Reference);
            save.Parameters.AddWithValue(order.Sku);
            save.Parameters.AddWithValue(order.Quantity);
            await save.ExecuteNonQueryAsync(token);
            string[] destinations = request.Scenario == "two-partners" ? ["primary", "secondary"] : ["primary"];
            ids.AddRange(await InsertEventAsync(connection, transaction, Guid.NewGuid(), orderId, order,
                id, request.Scenario, 1, destinations, token));
            if (request.Scenario == "out-of-order")
                ids.AddRange(await InsertEventAsync(connection, transaction, Guid.NewGuid(), orderId, order,
                    id, request.Scenario, 2, destinations, token));
        }
        await transaction.CommitAsync(token);
        return new(id, false, false, ids);
    }

    private static async Task<List<Guid>> RunDeliveryIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT d.id FROM lab_deliveries d JOIN lab_events e ON e.id=d.event_id WHERE e.run_id=$1 ORDER BY d.created_at,d.id", connection, transaction);
        command.Parameters.AddWithValue(id);
        await using var row = await command.ExecuteReaderAsync(token);
        List<Guid> ids = [];
        while (await row.ReadAsync(token)) ids.Add(row.GetGuid(0));
        return ids;
    }

    public async Task<List<RunSummary>> RunsAsync(CancellationToken token, Guid? id = null)
    {
        await using var command = source.CreateCommand("SELECT id,scenario,batch_size,paused,created_at FROM lab_runs WHERE ($1::uuid IS NULL OR id=$1) ORDER BY created_at DESC LIMIT 50");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)id ?? DBNull.Value });
        await using var row = await command.ExecuteReaderAsync(token);
        List<RunSummary> runs = [];
        while (await row.ReadAsync(token)) runs.Add(new(row.GetGuid(0), row.GetString(1), row.GetInt32(2), row.GetBoolean(3), row.GetFieldValue<DateTimeOffset>(4)));
        return runs;
    }

    public async Task<bool> SetRunPausedAsync(Guid id, bool paused, CancellationToken token)
    {
        await using var command = source.CreateCommand("UPDATE lab_runs SET paused=$2 WHERE id=$1");
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(paused);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }
}
