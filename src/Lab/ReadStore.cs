using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Octo.IntegrationLab.Lab;

public sealed record DeliveryView(Guid Id, Guid OrderId, string Reference, string Sku, int Quantity,
    string Status, int Attempts, int CycleAttempts, int Replays, DateTimeOffset NextAttemptAt, DateTimeOffset UpdatedAt,
    Guid EventId, string Destination, Guid? RunId, int Version);
public sealed record StateView(bool Paused, long Total, long Delivered, long Queued, long Exhausted,
    List<DeliveryView> Deliveries, long Matched, int Page, int PageSize);
public sealed record AttemptView(int Number, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string Outcome, int? HttpStatus, int? DurationMs, string? Detail);
public sealed record DeliveryDetail(Guid Id, JsonElement Payload, List<AttemptView> Attempts,
    string RawPayload, string PayloadHash, DeliveryView Delivery, ReceiverEvent? Receiver = null, bool ReceiverAvailable = false);

public sealed partial class LabStore
{
    public async Task<StateView> StateAsync(CancellationToken token, string search = "", string status = "", int page = 1, int pageSize = 100, Guid? runId = null)
    {
        page = Math.Clamp(page, 1, 1000000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var batch = source.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("""
            SELECT (SELECT paused FROM lab_settings WHERE id), (SELECT count(*) FROM lab_orders),
                count(*) FILTER (WHERE status = 'Delivered'),
                count(*) FILTER (WHERE status IN ('Pending','Retrying','Delivering')),
                count(*) FILTER (WHERE status = 'Exhausted') FROM lab_deliveries
            """));
        var command = new NpgsqlBatchCommand("""
            SELECT d.id, o.id, o.reference, o.sku, o.quantity, d.status, d.attempts, d.cycle_attempts,
                d.replays, d.next_attempt_at, d.updated_at, e.id, d.destination, e.run_id, e.version
            FROM lab_deliveries d JOIN lab_orders o ON o.id = d.order_id JOIN lab_events e ON e.id=d.event_id
            WHERE ($1 = '' OR position(lower($1) in lower(o.reference || ' ' || d.id::text || ' ' || e.id::text || ' ' || o.sku)) > 0)
                AND ($2 = '' OR d.status = $2)
                AND ($5::uuid IS NULL OR e.run_id=$5)
            ORDER BY d.created_at DESC, d.id LIMIT $3 OFFSET $4
            """);
        command.Parameters.AddWithValue(search);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(pageSize);
        command.Parameters.AddWithValue((page - 1) * pageSize);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)runId ?? DBNull.Value });
        batch.BatchCommands.Add(command);
        var count = new NpgsqlBatchCommand("""
            SELECT count(*) FROM lab_deliveries d JOIN lab_orders o ON o.id = d.order_id JOIN lab_events e ON e.id=d.event_id
            WHERE ($1 = '' OR position(lower($1) in lower(o.reference || ' ' || d.id::text || ' ' || e.id::text || ' ' || o.sku)) > 0)
                AND ($2 = '' OR d.status = $2)
                AND ($3::uuid IS NULL OR e.run_id=$3)
            """);
        count.Parameters.AddWithValue(search);
        count.Parameters.AddWithValue(status);
        count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)runId ?? DBNull.Value });
        batch.BatchCommands.Add(count);
        await using var reader = await batch.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var paused = reader.GetBoolean(0);
        var total = reader.GetInt64(1);
        var delivered = reader.GetInt64(2);
        var queued = reader.GetInt64(3);
        var exhausted = reader.GetInt64(4);
        await reader.NextResultAsync(token);
        List<DeliveryView> deliveries = [];
        while (await reader.ReadAsync(token)) deliveries.Add(ReadDelivery(reader));
        await reader.NextResultAsync(token);
        await reader.ReadAsync(token);
        return new(paused, total, delivered, queued, exhausted, deliveries, reader.GetInt64(0), page, pageSize);
    }

    public async Task<DeliveryDetail?> DetailAsync(Guid id, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var payloadQuery = new NpgsqlCommand("SELECT e.payload FROM lab_events e JOIN lab_deliveries d ON d.event_id=e.id WHERE d.id=$1", connection);
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
        await row.CloseAsync();
        await using var deliveryQuery = new Npgsql.NpgsqlCommand("""
            SELECT d.id, o.id, o.reference, o.sku, o.quantity, d.status, d.attempts, d.cycle_attempts,
                d.replays, d.next_attempt_at, d.updated_at, e.id, d.destination, e.run_id, e.version
            FROM lab_deliveries d JOIN lab_orders o ON o.id=d.order_id JOIN lab_events e ON e.id=d.event_id WHERE d.id=$1
            """, connection);
        deliveryQuery.Parameters.AddWithValue(id);
        await using var delivery = await deliveryQuery.ExecuteReaderAsync(token);
        if (!await delivery.ReadAsync(token)) return null;
        var view = ReadDelivery(delivery);
        return new(id, payload, attempts, body, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), view);
    }

    private static DeliveryView ReadDelivery(NpgsqlDataReader row) => new(row.GetGuid(0), row.GetGuid(1),
        row.GetString(2), row.GetString(3), row.GetInt32(4), row.GetString(5), row.GetInt32(6), row.GetInt32(7),
        row.GetInt32(8), row.GetFieldValue<DateTimeOffset>(9), row.GetFieldValue<DateTimeOffset>(10),
        row.GetGuid(11), row.GetString(12), row.IsDBNull(13) ? null : row.GetGuid(13), row.GetInt32(14));
}
