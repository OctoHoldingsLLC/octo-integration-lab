using Npgsql;
using NpgsqlTypes;

namespace Octo.IntegrationLab.Lab;

public sealed record QueueMetrics(Guid? RunId, long Queued, long InFlight, double? OldestQueuedSeconds,
    double? AverageDeliverySeconds, double? P95DeliverySeconds, long CurrentCycleRetries, long CompletedLastMinute);

public sealed partial class LabStore
{
    public async Task<QueueMetrics> MetricsAsync(Guid? runId, CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            WITH deliveries AS (
                SELECT d.* FROM lab_deliveries d JOIN lab_events e ON e.id=d.event_id
                WHERE ($1::uuid IS NULL OR e.run_id=$1)
            )
            SELECT count(*) FILTER (WHERE status IN ('Pending','Retrying')),
                count(*) FILTER (WHERE status='Delivering'),
                extract(epoch FROM clock_timestamp()-min(created_at) FILTER (WHERE status IN ('Pending','Retrying')))::double precision,
                avg(extract(epoch FROM updated_at-created_at)) FILTER (WHERE status='Delivered')::double precision,
                percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM updated_at-created_at)) FILTER (WHERE status='Delivered'),
                COALESCE(sum(greatest(cycle_attempts-1,0)),0)::bigint,
                count(*) FILTER (WHERE status='Delivered' AND updated_at >= clock_timestamp()-interval '60 seconds')
            FROM deliveries
            """);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)runId ?? DBNull.Value });
        await using var row = await command.ExecuteReaderAsync(token);
        await row.ReadAsync(token);
        return new(runId, row.GetInt64(0), row.GetInt64(1), row.IsDBNull(2) ? null : row.GetDouble(2),
            row.IsDBNull(3) ? null : row.GetDouble(3), row.IsDBNull(4) ? null : row.GetDouble(4), row.GetInt64(5), row.GetInt64(6));
    }

    public async Task RecordTraceAsync(DeliveryClaim claim, string traceId, CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            UPDATE lab_attempts a SET trace_id=$1 FROM lab_deliveries d
            WHERE a.delivery_id=d.id AND d.id=$2 AND d.claim_token=$3 AND d.status='Delivering' AND a.number=$4
            """);
        command.Parameters.AddWithValue(traceId);
        command.Parameters.AddWithValue(claim.Id);
        command.Parameters.AddWithValue(claim.Token);
        command.Parameters.AddWithValue(claim.Attempt);
        await command.ExecuteNonQueryAsync(token);
    }
}
