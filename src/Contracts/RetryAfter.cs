using System.Globalization;

namespace Octo.IntegrationLab;

public sealed record RetryAfterResult(TimeSpan? Delay, string Explanation);

public static class RetryAfter
{
    public static RetryAfterResult Parse(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return new(null, "No receiver wait requested.");
        double seconds;
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)) seconds = numeric;
        else if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            seconds = Math.Max(0, (date - now).TotalSeconds);
        else return new(null, "Invalid Retry-After ignored; using local backoff.");
        // Keep this interactive local lab bounded. Make the cap explicit in the attempt record.
        return new(TimeSpan.FromSeconds(Math.Min(seconds, 60)), seconds > 60
            ? "Receiver wait capped at the lab's 60-second limit." : "Receiver Retry-After respected.");
    }

    public static DeliveryDecision Apply(DeliveryDecision decision, RetryAfterResult hint) =>
        decision.RetryAfter is { } local && hint.Delay is { } remote
            ? decision with { RetryAfter = local > remote ? local : remote } : decision;
}
