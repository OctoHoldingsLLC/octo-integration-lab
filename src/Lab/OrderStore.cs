using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Octo.IntegrationLab.Lab;

public sealed record CreateOrderResult(Guid OrderId, Guid EventId, bool Reused, bool Conflict);

public sealed partial class LabStore
{
    public async Task<CreateOrderResult> CreateOrderAsync(string requestKey, OrderRequest request, CancellationToken token)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, LabJson.Options))));
        var orderId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO lab_orders (id, request_key, request_hash, reference, sku, quantity)
            VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (request_key) DO NOTHING RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue(orderId);
        insert.Parameters.AddWithValue(requestKey);
        insert.Parameters.AddWithValue(hash);
        insert.Parameters.AddWithValue(request.Reference);
        insert.Parameters.AddWithValue(request.Sku);
        insert.Parameters.AddWithValue(request.Quantity);
        if (await insert.ExecuteScalarAsync(token) is null)
        {
            // The unique key waits for concurrent inserts; this new statement sees their committed event too.
            await using var existing = new NpgsqlCommand("""
                SELECT o.id, e.id, o.request_hash FROM lab_orders o
                JOIN lab_events e ON e.order_id = o.id AND e.version=1 WHERE o.request_key = $1
                """, connection, transaction);
            existing.Parameters.AddWithValue(requestKey);
            await using var row = await existing.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) throw new InvalidOperationException("Idempotent order has no outbox event.");
            return new(row.GetGuid(0), row.GetGuid(1), true, row.GetString(2) != hash);
        }

        await InsertEventAsync(connection, transaction, eventId, orderId, request, null, null, 1, ["primary"], token);
        await transaction.CommitAsync(token);
        return new(orderId, eventId, false, false);
    }

    private static async Task<List<Guid>> InsertEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid eventId, Guid orderId, OrderRequest request, Guid? runId, string? scenario, int version,
        string[] destinations, CancellationToken token)
    {
        var status = version == 1 ? "confirmed" : "cancelled";
        var payload = JsonSerializer.Serialize(new OrderEvent(eventId, $"order.{status}", DateTimeOffset.UtcNow,
            new(orderId, request.Reference, request.Sku, request.Quantity, status, version), runId, scenario), LabJson.Options);
        await using var outbox = new NpgsqlCommand("""
            INSERT INTO lab_events (id, order_id, payload, run_id, version, trace_parent, trace_state) VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, connection, transaction);
        outbox.Parameters.AddWithValue(eventId);
        outbox.Parameters.AddWithValue(orderId);
        outbox.Parameters.AddWithValue(payload);
        outbox.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)runId ?? DBNull.Value });
        outbox.Parameters.AddWithValue(version);
        outbox.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)Activity.Current?.Id ?? DBNull.Value });
        outbox.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)Activity.Current?.TraceStateString ?? DBNull.Value });
        await outbox.ExecuteNonQueryAsync(token);
        List<Guid> deliveryIds = [];
        foreach (var destination in destinations)
        {
            var deliveryId = destination == "primary" ? eventId : Guid.NewGuid();
            await using var delivery = new NpgsqlCommand("""
                INSERT INTO lab_deliveries(id,order_id,event_id,destination,next_attempt_at)
                VALUES($1,$2,$3,$4,clock_timestamp()+make_interval(secs=>$5))
                """, connection, transaction);
            delivery.Parameters.AddWithValue(deliveryId);
            delivery.Parameters.AddWithValue(orderId);
            delivery.Parameters.AddWithValue(eventId);
            delivery.Parameters.AddWithValue(destination);
            delivery.Parameters.AddWithValue(scenario == "out-of-order" && version == 1 ? 3 : 0);
            await delivery.ExecuteNonQueryAsync(token);
            deliveryIds.Add(deliveryId);
        }
        return deliveryIds;
    }
}
