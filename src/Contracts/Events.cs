using System.Text.Json;

namespace Octo.IntegrationLab;

public static class LabJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed record OrderRequest(string Reference, string Sku, int Quantity);
public sealed record OrderData(Guid Id, string Reference, string Sku, int Quantity, string Status, int Version = 1);
public sealed record OrderEvent(Guid Id, string Type, DateTimeOffset OccurredAt, OrderData Data,
    Guid? RunId = null, string? Scenario = null);
public sealed record PartnerControl(string Mode, int DelayMs = 5000)
{
    public static readonly string[] Modes = ["healthy", "reject", "slow", "offline", "lost-ack"];
    public bool IsValid => Modes.Contains(Mode) && DelayMs is >= 3000 and <= 15000;
}

public sealed record ReceiptResult(Guid EventId, bool Duplicate, bool Stale = false);
public sealed record RunControl(string Mode);
public sealed record ReceiverObservation(DateTimeOffset At, string Outcome, string Detail, int? HttpStatus, string? TraceId);
public sealed record ReceiverEvent(Guid EventId, bool Received, string? Effect, int Duplicates,
    string? OrderStatus, int? OrderVersion, List<ReceiverObservation> Observations);

public sealed record DeliveryDecision(string Status, TimeSpan? RetryAfter);

public static class DeliveryPolicy
{
    public static DeliveryDecision Decide(bool success, int? httpStatus, int cycleAttempt,
        int maxAttempts, int baseDelaySeconds, double jitter)
    {
        if (success) return new("Delivered", null);
        var transient = httpStatus is null or 408 or 425 or 429 or >= 500;
        if (!transient || cycleAttempt >= maxAttempts) return new("Exhausted", null);
        var seconds = Math.Min(60, baseDelaySeconds * Math.Pow(2, Math.Min(cycleAttempt - 1, 10)));
        return new("Retrying", TimeSpan.FromSeconds(Math.Min(60, seconds * (1 + Math.Clamp(jitter, 0, 0.25)))));
    }
}
