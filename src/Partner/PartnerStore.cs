using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Octo.IntegrationLab.Storage;

namespace Octo.IntegrationLab.Partner;

public sealed class PartnerStore(NpgsqlDataSource source)
{
    public Task InitializeAsync() =>
        SchemaMigrator.ApplyAsync(source, Assembly.GetExecutingAssembly(), "partner", 771002);

    public async Task<PartnerControl> ControlAsync(CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT mode, delay_ms FROM partner_settings WHERE id");
        await using var row = await command.ExecuteReaderAsync(token);
        await row.ReadAsync(token);
        return new(row.GetString(0), row.GetInt32(1));
    }

    public async Task SetControlAsync(PartnerControl control, CancellationToken token)
    {
        await using var command = source.CreateCommand("UPDATE partner_settings SET mode = $1, delay_ms = $2 WHERE id");
        command.Parameters.AddWithValue(control.Mode);
        command.Parameters.AddWithValue(control.DelayMs);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<InboxResult> AcceptAsync(OrderEvent message, string body, CancellationToken token)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var receipt = new NpgsqlCommand("""
            INSERT INTO partner_receipts (event_id, payload_hash, order_id, reference) VALUES ($1, $2, $3, $4)
            ON CONFLICT (event_id) DO NOTHING RETURNING event_id
            """, connection, transaction);
        receipt.Parameters.AddWithValue(message.Id);
        receipt.Parameters.AddWithValue(hash);
        receipt.Parameters.AddWithValue(message.Data.Id);
        receipt.Parameters.AddWithValue(message.Data.Reference);
        if (await receipt.ExecuteScalarAsync(token) is null)
        {
            await using var duplicate = new NpgsqlCommand("""
                UPDATE partner_receipts SET duplicates = duplicates + 1 WHERE event_id = $1 AND payload_hash = $2
                """, connection, transaction);
            duplicate.Parameters.AddWithValue(message.Id);
            duplicate.Parameters.AddWithValue(hash);
            var matched = await duplicate.ExecuteNonQueryAsync(token) == 1;
            await transaction.CommitAsync(token);
            return matched ? InboxResult.Duplicate : InboxResult.Conflict;
        }
        await using var order = new NpgsqlCommand("""
            INSERT INTO partner_orders (order_id, event_id, reference, sku, quantity) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (order_id) DO NOTHING
            """, connection, transaction);
        order.Parameters.AddWithValue(message.Data.Id);
        order.Parameters.AddWithValue(message.Id);
        order.Parameters.AddWithValue(message.Data.Reference);
        order.Parameters.AddWithValue(message.Data.Sku);
        order.Parameters.AddWithValue(message.Data.Quantity);
        // A different event cannot silently overwrite an existing order's side effect.
        if (await order.ExecuteNonQueryAsync(token) != 1) return InboxResult.Conflict;
        await transaction.CommitAsync(token);
        return InboxResult.Accepted;
    }

    public async Task<object> StateAsync(CancellationToken token)
    {
        var control = await ControlAsync(token);
        await using var command = source.CreateCommand("""
            SELECT count(*), COALESCE(sum(duplicates),0)::bigint FROM partner_receipts;
            SELECT event_id, order_id, reference, received_at, duplicates FROM partner_receipts
            ORDER BY received_at DESC LIMIT 100;
            SELECT count(*) FROM partner_orders
            """);
        await using var row = await command.ExecuteReaderAsync(token);
        await row.ReadAsync(token);
        var accepted = row.GetInt64(0);
        var duplicateCount = row.GetInt64(1);
        await row.NextResultAsync(token);
        List<object> receipts = [];
        while (await row.ReadAsync(token)) receipts.Add(new
        {
            eventId = row.GetGuid(0),
            orderId = row.GetGuid(1),
            reference = row.GetString(2),
            receivedAt = row.GetFieldValue<DateTimeOffset>(3),
            duplicates = row.GetInt32(4)
        });
        await row.NextResultAsync(token);
        await row.ReadAsync(token);
        return new { control.Mode, control.DelayMs, accepted, duplicates = duplicateCount, processed = row.GetInt64(0), receipts };
    }
}

public enum InboxResult { Accepted, Duplicate, Conflict }
