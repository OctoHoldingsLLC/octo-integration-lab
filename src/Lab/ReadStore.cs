using System.Text.Json;

namespace Octo.IntegrationLab.Lab;

public sealed record DeliveryView(Guid Id, Guid OrderId, string Reference, string Sku, int Quantity,
    string Status, int Attempts, int CycleAttempts, int Replays, DateTimeOffset NextAttemptAt, DateTimeOffset UpdatedAt);
public sealed record StateView(bool Paused, long Total, long Delivered, long Queued, long Exhausted, List<DeliveryView> Deliveries);
public sealed record AttemptView(int Number, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string Outcome, int? HttpStatus, int? DurationMs, string? Detail);
public sealed record DeliveryDetail(Guid Id, JsonElement Payload, List<AttemptView> Attempts);

public sealed partial class LabStore
{
    public async Task<StateView> StateAsync(CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT paused FROM lab_settings WHERE id), count(*),
                count(*) FILTER (WHERE status = 'Delivered'),
                count(*) FILTER (WHERE status IN ('Pending','Retrying','Delivering')),
                count(*) FILTER (WHERE status = 'Exhausted') FROM lab_deliveries;
            SELECT d.id, o.id, o.reference, o.sku, o.quantity, d.status, d.attempts, d.cycle_attempts,
                d.replays, d.next_attempt_at, d.updated_at
            FROM lab_deliveries d JOIN lab_orders o ON o.id = d.order_id
            ORDER BY d.created_at DESC, d.id LIMIT 100
            """);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var paused = reader.GetBoolean(0);
        var total = reader.GetInt64(1);
        var delivered = reader.GetInt64(2);
        var queued = reader.GetInt64(3);
        var exhausted = reader.GetInt64(4);
        await reader.NextResultAsync(token);
        List<DeliveryView> deliveries = [];
        while (await reader.ReadAsync(token)) deliveries.Add(new(reader.GetGuid(0), reader.GetGuid(1),
            reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.GetInt32(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetFieldValue<DateTimeOffset>(9), reader.GetFieldValue<DateTimeOffset>(10)));
        return new(paused, total, delivered, queued, exhausted, deliveries);
    }

    public async Task<DeliveryDetail?> DetailAsync(Guid id, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var payloadQuery = new Npgsql.NpgsqlCommand("SELECT payload FROM lab_deliveries WHERE id = $1", connection);
        payloadQuery.Parameters.AddWithValue(id);
        if (await payloadQuery.ExecuteScalarAsync(token) is not string body) return null;
        using var document = JsonDocument.Parse(body);
        var payload = document.RootElement.Clone();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT number, started_at, completed_at, outcome, http_status, duration_ms, detail
            FROM lab_attempts WHERE delivery_id = $1 ORDER BY number
            """, connection);
        command.Parameters.AddWithValue(id);
        await using var row = await command.ExecuteReaderAsync(token);
        List<AttemptView> attempts = [];
        while (await row.ReadAsync(token)) attempts.Add(new(row.GetInt32(0), row.GetFieldValue<DateTimeOffset>(1),
            row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2), row.GetString(3),
            row.IsDBNull(4) ? null : row.GetInt32(4), row.IsDBNull(5) ? null : row.GetInt32(5),
            row.IsDBNull(6) ? null : row.GetString(6)));
        return new(id, payload, attempts);
    }
}
