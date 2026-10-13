using Npgsql;

namespace Octo.IntegrationLab.Lab;

public sealed record CircuitTransition(string State, string Reason, DateTimeOffset At);
public sealed record CircuitView(string Destination, string State, DateTimeOffset? OpenUntil, List<CircuitTransition> History);

public sealed partial class LabStore
{
    private static async Task<bool> EnterCircuitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeliveryClaim claim, int leaseSeconds, CancellationToken token)
    {
        if (claim.RunId is null) return true;
        await using var gate = new NpgsqlCommand("""
            SELECT state, (open_until > clock_timestamp()), (probe_lease_until > clock_timestamp())
            FROM lab_circuits WHERE run_id=$1 AND destination=$2 FOR UPDATE
            """, connection, transaction);
        gate.Parameters.AddWithValue(claim.RunId.Value);
        gate.Parameters.AddWithValue(claim.Destination);
        await using (var row = await gate.ExecuteReaderAsync(token))
        {
            if (!await row.ReadAsync(token)) return true;
            var state = row.GetString(0);
            if (state == "Closed") return true;
            if (state == "Open" && !row.IsDBNull(1) && row.GetBoolean(1)) return false;
            if (state == "HalfOpen" && !row.IsDBNull(2) && row.GetBoolean(2)) return false;
        }
        await using var probe = new NpgsqlCommand("""
            UPDATE lab_circuits SET state='HalfOpen',probe_token=$3,
                probe_lease_until=clock_timestamp()+make_interval(secs=>$4)
            WHERE run_id=$1 AND destination=$2
            """, connection, transaction);
        probe.Parameters.AddWithValue(claim.RunId.Value);
        probe.Parameters.AddWithValue(claim.Destination);
        probe.Parameters.AddWithValue(claim.Token);
        probe.Parameters.AddWithValue(leaseSeconds);
        await probe.ExecuteNonQueryAsync(token);
        await CircuitHistoryAsync(connection, transaction, claim, "HalfOpen", "Wait ended; one leased recovery probe may send.", token);
        return true;
    }

    private static async Task FinishCircuitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeliveryClaim claim, DeliveryDecision decision, int? httpStatus, CancellationToken token)
    {
        if (claim.RunId is null) return;
        await using var gate = new NpgsqlCommand("""
            SELECT state,failures,probe_token FROM lab_circuits WHERE run_id=$1 AND destination=$2 FOR UPDATE
            """, connection, transaction);
        gate.Parameters.AddWithValue(claim.RunId.Value);
        gate.Parameters.AddWithValue(claim.Destination);
        string oldState;
        int failures;
        bool probe;
        await using (var row = await gate.ExecuteReaderAsync(token))
        {
            if (!await row.ReadAsync(token)) return;
            oldState = row.GetString(0);
            failures = row.GetInt32(1);
            probe = !row.IsDBNull(2) && row.GetGuid(2) == claim.Token;
        }
        if (oldState != "Closed" && !probe) return;
        var unavailable = decision.Status != "Delivered" && httpStatus is null or 408 or 425 or 429 or >= 500;
        failures = unavailable ? failures + 1 : 0;
        var state = unavailable && (probe || failures >= 2) ? "Open" : "Closed";
        await using var update = new NpgsqlCommand("""
            UPDATE lab_circuits SET state=$3,failures=$4,probe_token=NULL,probe_lease_until=NULL,
                open_until=CASE WHEN $3='Open' THEN clock_timestamp()+interval '5 seconds' ELSE NULL END
            WHERE run_id=$1 AND destination=$2
            """, connection, transaction);
        update.Parameters.AddWithValue(claim.RunId.Value);
        update.Parameters.AddWithValue(claim.Destination);
        update.Parameters.AddWithValue(state);
        update.Parameters.AddWithValue(failures);
        await update.ExecuteNonQueryAsync(token);
        if (oldState != state) await CircuitHistoryAsync(connection, transaction, claim, state,
            state == "Open" ? "Receiver unavailable; hold new sends for five seconds without consuming attempts."
                : "Recovery probe reached the receiver; resume queued deliveries.", token);
    }

    private static async Task CircuitHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeliveryClaim claim, string state, string reason, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("INSERT INTO lab_circuit_history(run_id,destination,state,reason) VALUES($1,$2,$3,$4)", connection, transaction);
        command.Parameters.AddWithValue(claim.RunId!.Value);
        command.Parameters.AddWithValue(claim.Destination);
        command.Parameters.AddWithValue(state);
        command.Parameters.AddWithValue(reason);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<List<CircuitView>> CircuitsAsync(Guid id, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT destination,state,open_until FROM lab_circuits WHERE run_id=$1");
        command.Parameters.AddWithValue(id);
        List<CircuitView> circuits = [];
        await using (var row = await command.ExecuteReaderAsync(token))
            while (await row.ReadAsync(token)) circuits.Add(new(row.GetString(0), row.GetString(1),
                row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2), []));
        foreach (var circuit in circuits)
        {
            await using var history = source.CreateCommand("SELECT state,reason,at FROM lab_circuit_history WHERE run_id=$1 AND destination=$2 ORDER BY id");
            history.Parameters.AddWithValue(id);
            history.Parameters.AddWithValue(circuit.Destination);
            await using var row = await history.ExecuteReaderAsync(token);
            while (await row.ReadAsync(token)) circuit.History.Add(new(row.GetString(0), row.GetString(1), row.GetFieldValue<DateTimeOffset>(2)));
        }
        return circuits;
    }
}
