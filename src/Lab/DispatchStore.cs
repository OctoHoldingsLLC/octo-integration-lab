using Npgsql;
using NpgsqlTypes;

namespace Octo.IntegrationLab.Lab;

public sealed record DeliveryClaim(Guid Id, Guid Token, string Payload, int Attempt, int CycleAttempt);

public sealed partial class LabStore
{
    public async Task RecoverExpiredAsync(int maxAttempts, CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            WITH expired AS (
                SELECT id FROM lab_deliveries WHERE status = 'Delivering' AND lease_until < clock_timestamp()
                FOR UPDATE SKIP LOCKED
            ), recovered AS (
                UPDATE lab_deliveries d SET
                    status = CASE WHEN cycle_attempts >= $1 THEN 'Exhausted' ELSE 'Retrying' END,
                    claim_token = NULL, lease_until = NULL, next_attempt_at = clock_timestamp(),
                    updated_at = clock_timestamp()
                FROM expired e WHERE d.id = e.id RETURNING d.id, d.attempts
            )
            UPDATE lab_attempts a SET outcome = 'Interrupted', completed_at = clock_timestamp(),
                detail = 'Worker lease expired. The receiver may have accepted this event; a retry can be a duplicate.'
            FROM recovered r WHERE a.delivery_id = r.id AND a.number = r.attempts AND a.completed_at IS NULL
            """);
        command.Parameters.AddWithValue(maxAttempts);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<DeliveryClaim?> ClaimAsync(int leaseSeconds, CancellationToken token)
    {
        var claimToken = Guid.NewGuid();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = new NpgsqlCommand("""
            WITH candidate AS (
                SELECT id FROM lab_deliveries
                WHERE status IN ('Pending','Retrying') AND next_attempt_at <= clock_timestamp()
                    AND NOT (SELECT paused FROM lab_settings WHERE id)
                ORDER BY next_attempt_at, created_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE lab_deliveries d SET status = 'Delivering', attempts = attempts + 1,
                cycle_attempts = cycle_attempts + 1, claim_token = $1,
                lease_until = clock_timestamp() + make_interval(secs => $2), updated_at = clock_timestamp()
            FROM candidate c WHERE d.id = c.id
            RETURNING d.id, d.payload, d.attempts, d.cycle_attempts
            """, connection, transaction);
        command.Parameters.AddWithValue(claimToken);
        command.Parameters.AddWithValue(leaseSeconds);
        DeliveryClaim claim;
        await using (var row = await command.ExecuteReaderAsync(token))
        {
            if (!await row.ReadAsync(token)) return null;
            claim = new(row.GetGuid(0), claimToken, row.GetString(1), row.GetInt32(2), row.GetInt32(3));
        }
        await using var attempt = new NpgsqlCommand("""
            INSERT INTO lab_attempts (id, delivery_id, number) VALUES ($1, $2, $3)
            """, connection, transaction);
        attempt.Parameters.AddWithValue(Guid.NewGuid());
        attempt.Parameters.AddWithValue(claim.Id);
        attempt.Parameters.AddWithValue(claim.Attempt);
        await attempt.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return claim;
    }

    public async Task CompleteAsync(DeliveryClaim claim, DeliveryDecision decision, string outcome,
        int? httpStatus, int durationMs, string detail, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var finish = new NpgsqlCommand("""
            UPDATE lab_deliveries SET status = $1, claim_token = NULL, lease_until = NULL,
                next_attempt_at = clock_timestamp() + make_interval(secs => $2), updated_at = clock_timestamp()
            WHERE id = $3 AND claim_token = $4 AND status = 'Delivering'
            """, connection, transaction);
        finish.Parameters.AddWithValue(decision.Status);
        finish.Parameters.AddWithValue(decision.RetryAfter?.TotalSeconds ?? 0);
        finish.Parameters.AddWithValue(claim.Id);
        finish.Parameters.AddWithValue(claim.Token);
        // A stale worker cannot overwrite a claim recovered by another worker.
        if (await finish.ExecuteNonQueryAsync(token) != 1) return;
        await using var attempt = new NpgsqlCommand("""
            UPDATE lab_attempts SET completed_at = clock_timestamp(), outcome = $1, http_status = $2,
                duration_ms = $3, detail = $4 WHERE delivery_id = $5 AND number = $6
            """, connection, transaction);
        attempt.Parameters.AddWithValue(outcome);
        attempt.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)httpStatus ?? DBNull.Value });
        attempt.Parameters.AddWithValue(durationMs);
        attempt.Parameters.AddWithValue(detail);
        attempt.Parameters.AddWithValue(claim.Id);
        attempt.Parameters.AddWithValue(claim.Attempt);
        await attempt.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }
}
