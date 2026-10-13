using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Octo.IntegrationLab.Storage;

/// <summary>Transactional, checksummed migrations, serialized separately for each service.</summary>
public static class SchemaMigrator
{
    public static async Task ApplyAsync(NpgsqlDataSource source, Assembly assembly, string owner,
        long lockId, CancellationToken token = default)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(771000), pg_advisory_xact_lock($1)", connection, transaction);
        guard.Parameters.AddWithValue(lockId);
        await guard.ExecuteNonQueryAsync(token);
        await using var ledger = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS schema_versions (
                owner text NOT NULL, name text NOT NULL, checksum text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp(), PRIMARY KEY(owner, name)
            )
            """, connection, transaction);
        await ledger.ExecuteNonQueryAsync(token);
        var names = assembly.GetManifestResourceNames();
        // Adopt pre-migration volumes using the unchanged, idempotent baseline.
        var migrations = names.Where(name => name.EndsWith(".schema.sql", StringComparison.Ordinal))
            .Concat(names.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        foreach (var name in migrations)
        {
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(token);
            var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));
            await using var existing = new NpgsqlCommand("SELECT checksum FROM schema_versions WHERE owner=$1 AND name=$2", connection, transaction);
            existing.Parameters.AddWithValue(owner);
            existing.Parameters.AddWithValue(name);
            if (await existing.ExecuteScalarAsync(token) is string applied)
            {
                if (applied != checksum) throw new InvalidOperationException($"Applied migration {name} was modified. Add a new migration instead.");
                continue;
            }
            await using var change = new NpgsqlCommand(sql, connection, transaction);
            await change.ExecuteNonQueryAsync(token);
            await using var record = new NpgsqlCommand("INSERT INTO schema_versions(owner,name,checksum) VALUES($1,$2,$3)", connection, transaction);
            record.Parameters.AddWithValue(owner);
            record.Parameters.AddWithValue(name);
            record.Parameters.AddWithValue(checksum);
            await record.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }
}
