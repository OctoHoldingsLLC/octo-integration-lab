using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace Octo.IntegrationLab.Partner;

public sealed partial class PartnerStore
{
    public async Task<int> BeginRequestAsync(OrderEvent message, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO partner_requests(event_id) VALUES($1) ON CONFLICT(event_id)
            DO UPDATE SET requests=partner_requests.requests+1 RETURNING requests
            """, connection, transaction);
        command.Parameters.AddWithValue(message.Id);
        var number = (int)(await command.ExecuteScalarAsync(token))!;
        await ObserveAsync(connection, transaction, message, "Received", $"Signed delivery {number} received and verified.", null, token);
        await transaction.CommitAsync(token);
        return number;
    }

    public async Task ObserveAsync(OrderEvent message, string outcome, string detail, int? status, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await ObserveAsync(connection, null, message, outcome, detail, status, token);
    }

    private static async Task ObserveAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        OrderEvent message, string outcome, string detail, int? status, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO partner_observations(event_id,run_id,outcome,detail,http_status,trace_id) VALUES($1,$2,$3,$4,$5,$6)
            """, connection, transaction);
        command.Parameters.AddWithValue(message.Id);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)message.RunId ?? DBNull.Value });
        command.Parameters.AddWithValue(outcome);
        command.Parameters.AddWithValue(detail);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)Activity.Current?.TraceId.ToString() ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<string?> RunModeAsync(Guid id, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT mode FROM partner_run_controls WHERE run_id=$1");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(token) as string;
    }

    public async Task SetRunModeAsync(Guid id, string mode, CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO partner_run_controls(run_id,mode) VALUES($1,$2)
            ON CONFLICT(run_id) DO UPDATE SET mode=EXCLUDED.mode
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(mode);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<ReceiverEvent> EventAsync(Guid id, CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            SELECT r.effect,r.duplicates,o.status,o.version
            FROM partner_receipts r LEFT JOIN partner_orders o ON o.order_id=r.order_id WHERE r.event_id=$1
            """);
        command.Parameters.AddWithValue(id);
        bool received;
        string? effect = null, status = null;
        int duplicates = 0;
        int? version = null;
        await using (var row = await command.ExecuteReaderAsync(token))
        {
            received = await row.ReadAsync(token);
            if (received)
            {
                effect = row.GetString(0);
                duplicates = row.GetInt32(1);
                status = row.IsDBNull(2) ? null : row.GetString(2);
                version = row.IsDBNull(3) ? null : row.GetInt32(3);
            }
        }
        await using var observations = source.CreateCommand("""
            SELECT observed_at,outcome,detail,http_status,trace_id FROM partner_observations
            WHERE event_id=$1 ORDER BY observed_at,id
            """);
        observations.Parameters.AddWithValue(id);
        await using var entries = await observations.ExecuteReaderAsync(token);
        List<ReceiverObservation> history = [];
        while (await entries.ReadAsync(token)) history.Add(new(entries.GetFieldValue<DateTimeOffset>(0), entries.GetString(1),
            entries.GetString(2), entries.IsDBNull(3) ? null : entries.GetInt32(3), entries.IsDBNull(4) ? null : entries.GetString(4)));
        return new(id, received, effect, duplicates, status, version, history);
    }

    public async Task<List<ReceiverEvent>> RunAsync(Guid id, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT DISTINCT event_id FROM partner_observations WHERE run_id=$1");
        command.Parameters.AddWithValue(id);
        List<Guid> ids = [];
        await using (var row = await command.ExecuteReaderAsync(token))
            while (await row.ReadAsync(token)) ids.Add(row.GetGuid(0));
        List<ReceiverEvent> events = [];
        foreach (var eventId in ids) events.Add(await EventAsync(eventId, token));
        return events;
    }
}
