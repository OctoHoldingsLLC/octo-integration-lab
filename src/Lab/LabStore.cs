using System.Reflection;
using Npgsql;

namespace Octo.IntegrationLab.Lab;

public sealed partial class LabStore(NpgsqlDataSource source)
{
    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(771001)", connection, transaction);
        await guard.ExecuteNonQueryAsync(token);
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lab.schema.sql")!;
        using var reader = new StreamReader(stream);
        await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(token), connection, transaction);
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task SetPausedAsync(bool paused, CancellationToken token)
    {
        await using var command = source.CreateCommand("UPDATE lab_settings SET paused = $1 WHERE id");
        command.Parameters.AddWithValue(paused);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<bool> ReplayAsync(Guid id, CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            UPDATE lab_deliveries SET status = 'Pending', cycle_attempts = 0, replays = replays + 1,
                next_attempt_at = clock_timestamp(), updated_at = clock_timestamp()
            WHERE id = $1 AND status IN ('Delivered','Exhausted')
            """);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }
}
