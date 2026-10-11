using System.Security.Cryptography;
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
                SELECT o.id, d.id, o.request_hash FROM lab_orders o
                JOIN lab_deliveries d ON d.order_id = o.id WHERE o.request_key = $1
                """, connection, transaction);
            existing.Parameters.AddWithValue(requestKey);
            await using var row = await existing.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) throw new InvalidOperationException("Idempotent order has no outbox event.");
            return new(row.GetGuid(0), row.GetGuid(1), true, row.GetString(2) != hash);
        }

        var payload = JsonSerializer.Serialize(new OrderEvent(eventId, "order.confirmed", DateTimeOffset.UtcNow,
            new(orderId, request.Reference, request.Sku, request.Quantity, "confirmed")), LabJson.Options);
        await using var outbox = new NpgsqlCommand("""
            INSERT INTO lab_deliveries (id, order_id, payload) VALUES ($1, $2, $3)
            """, connection, transaction);
        outbox.Parameters.AddWithValue(eventId);
        outbox.Parameters.AddWithValue(orderId);
        outbox.Parameters.AddWithValue(payload);
        await outbox.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return new(orderId, eventId, false, false);
    }
}
