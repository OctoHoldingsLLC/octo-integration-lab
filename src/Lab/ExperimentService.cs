namespace Octo.IntegrationLab.Lab;

public sealed record ExperimentStep(string Label, bool Complete);
public sealed record RunView(RunSummary Run, ExperimentDefinition Definition, List<DeliveryView> Deliveries,
    Dictionary<string, List<ReceiverEvent>?> Receivers, List<ExperimentStep> Steps, bool Complete, bool GlobalPaused,
    List<CircuitView> Circuits);

public sealed class ExperimentService(LabStore store, PartnerClient partners)
{
    public async Task<RunView?> ReadAsync(Guid id, CancellationToken token)
    {
        var run = (await store.RunsAsync(token, id)).SingleOrDefault();
        if (run is null) return null;
        var state = await store.StateAsync(token, runId: id);
        var attempts = await store.RunAttemptsAsync(id, token);
        var circuits = await store.CircuitsAsync(id, token);
        Dictionary<string, List<ReceiverEvent>?> receivers = [];
        foreach (var destination in state.Deliveries.Select(d => d.Destination).Distinct())
            receivers[destination] = await partners.RunAsync(destination, id, token);
        var observations = receivers.Values.Where(v => v is not null).SelectMany(v => v!)
            .SelectMany(e => e.Observations).ToList();
        bool hasDemonstrated;
        string demonstration;
        switch (run.Scenario)
        {
            case "reject":
                demonstration = "Observe a failed delivery, then restore this run's receiver";
                hasDemonstrated = attempts.Any(a => a.HttpStatus == 503);
                break;
            case "fail-twice":
                demonstration = "Observe two 503 responses before recovery";
                hasDemonstrated = attempts.Count(a => a.HttpStatus == 503) >= 2;
                break;
            case "rate-limit":
                demonstration = "Wait through both numeric and date Retry-After responses";
                hasDemonstrated = attempts.Count(a => a.HttpStatus == 429) >= 2;
                break;
            case "lost-ack":
                demonstration = "Observe a sender timeout and a receiver duplicate";
                hasDemonstrated = attempts.Any(a => a.Outcome == "Timeout") && observations.Any(o => o.Outcome == "Duplicate");
                break;
            case "pause":
                demonstration = "Resume the run without recreating its saved order";
                hasDemonstrated = !run.Paused && attempts.Count > 0;
                break;
            case "permanent":
                demonstration = "Stop after a permanent 422 validation rejection";
                hasDemonstrated = attempts.Any(a => a.HttpStatus == 422) && state.Deliveries.All(d => d.CycleAttempts == 1);
                break;
            case "out-of-order":
                demonstration = "Acknowledge the older confirmation without undoing cancellation";
                hasDemonstrated = observations.Any(o => o.Outcome == "Stale");
                break;
            case "two-partners":
                demonstration = "Deliver to the healthy partner while the second retries";
                hasDemonstrated = attempts.Count(a => a.HttpStatus == 503) >= 2 && receivers.Count == 2;
                break;
            case "circuit-breaker":
                demonstration = "Open the circuit, restore the receiver, and pass a recovery probe";
                hasDemonstrated = circuits.Any(c => c.State == "Closed" && c.History.Any(h => h.State == "Open")
                    && c.History.Any(h => h.State == "HalfOpen"));
                break;
            default:
                demonstration = $"Drain the batch of {run.BatchSize} orders";
                hasDemonstrated = state.Deliveries.Count == run.BatchSize && attempts.Count >= run.BatchSize;
                break;
        }
        var verified = receivers.Values.All(value => value is not null) && state.Deliveries.All(delivery =>
            delivery.Status == "Delivered" && receivers[delivery.Destination]!.Any(e => e.EventId == delivery.EventId && e.Received));
        if (run.Scenario == "permanent") verified = receivers.Values.All(v => v is not null)
            && state.Deliveries.All(d => d.Status == "Exhausted") && observations.Any(o => o.HttpStatus == 422)
            && receivers.Values.SelectMany(v => v!).All(e => !e.Received);
        if (run.Scenario == "out-of-order") verified &= receivers.Values.Where(v => v is not null).SelectMany(v => v!)
            .All(e => e.OrderVersion == 2 && e.OrderStatus == "cancelled");
        List<ExperimentStep> steps = [new("Save the run, order, and immutable events", state.Deliveries.Count > 0),
            new(demonstration, hasDemonstrated), new(run.Scenario == "permanent"
                ? "Verify exhaustion and no receiver business effect" : "Verify final delivery and durable receiver records", verified)];
        return new(run, Experiments.Find(run.Scenario)!, state.Deliveries, receivers, steps,
            steps.All(step => step.Complete), state.Paused, circuits);
    }

    public async Task<bool> RecoverAsync(Guid id, CancellationToken token)
    {
        if ((await store.RunsAsync(token, id)).Count == 0) return false;
        var deliveries = (await store.StateAsync(token, runId: id)).Deliveries;
        foreach (var destination in deliveries.Select(d => d.Destination).Distinct())
            if (!await partners.RecoverRunAsync(destination, id, token)) return false;
        foreach (var delivery in deliveries.Where(d => d.Status == "Exhausted")) await store.ReplayAsync(delivery.Id, token);
        await store.SetRunPausedAsync(id, false, token);
        return true;
    }
}
