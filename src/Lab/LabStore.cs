using System.Reflection;
using Npgsql;
using Octo.IntegrationLab.Storage;

namespace Octo.IntegrationLab.Lab;

public sealed partial class LabStore(NpgsqlDataSource source)
{
    public Task InitializeAsync(CancellationToken token = default) =>
        SchemaMigrator.ApplyAsync(source, Assembly.GetExecutingAssembly(), "lab", 771001, token);

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
