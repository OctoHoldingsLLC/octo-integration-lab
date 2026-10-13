namespace Octo.IntegrationLab;

public sealed record ExperimentDefinition(string Id, string Title, string Summary, string Lesson, int Orders = 1);
public sealed record ExperimentRequest(string Scenario, int? BatchSize = null);

public static class Experiments
{
    public static readonly ExperimentDefinition[] All =
    [
        new("reject", "Let it fail. Then fix it.", "Observe a 503, then restore this run's receiver.", "A durable retry keeps the same event identity through an outage."),
        new("lost-ack", "The reply never arrived.", "Commit the order, lose the reply, and retry.", "A timeout does not prove failure. The durable inbox prevents a second business effect."),
        new("pause", "Put delivery on hold.", "Save an order while this run is paused. Resume when ready.", "The order and its event survive independently of the worker process."),
        new("fail-twice", "Third time arrives.", "Two predictable failures, then an automatic recovery.", "Each retry reuses the event and waits longer before trying again."),
        new("rate-limit", "Give the receiver time.", "Respect both seconds and date forms of Retry-After.", "The sender combines its backoff with the receiver's requested waiting period."),
        new("permanent", "Some failures need a fix.", "Receive a permanent validation rejection.", "Repeating invalid input cannot repair it. A permanent rejection stops immediately."),
        new("burst", "Watch the queue drain.", "Send a bounded batch through three delivery workers.", "Concurrency limits the active work; the durable queue absorbs the burst.", 20),
        new("circuit-breaker", "Let the partner breathe.", "Open a circuit during an outage, then restore this run.", "An open circuit holds queued work without spending its retry budget. One probe tests recovery.", 12),
        new("out-of-order", "Cancellation arrives first.", "Deliver version two before version one.", "A receiver needs version checks as well as deduplication to avoid applying stale state."),
        new("two-partners", "One event, two destinations.", "Keep one partner healthy while the other fails twice.", "Each destination owns an independent delivery status and retry budget.")
    ];

    public static ExperimentDefinition? Find(string id) => All.FirstOrDefault(item => item.Id == id);
}
