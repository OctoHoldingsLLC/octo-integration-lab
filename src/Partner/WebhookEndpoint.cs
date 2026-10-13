using System.Globalization;
using System.Text.Json;

namespace Octo.IntegrationLab.Partner;

public sealed record PartnerRuntime(string Secret, string Name);

public static class WebhookEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, PartnerStore store, PartnerRuntime runtime)
    {
        var token = context.RequestAborted;
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(token);
        var eventId = context.Request.Headers[WebhookSignature.IdHeader].ToString();
        if (!WebhookSignature.Verify(runtime.Secret, context.Request.Headers[WebhookSignature.TimestampHeader].ToString(),
            eventId, body, context.Request.Headers[WebhookSignature.SignatureHeader].ToString(), DateTimeOffset.UtcNow))
            return Results.Json(new { error = "Invalid or expired signature." }, statusCode: 401);
        OrderEvent? message;
        try { message = JsonSerializer.Deserialize<OrderEvent>(body, LabJson.Options); }
        catch (JsonException) { return Results.BadRequest(new { error = "Invalid event JSON." }); }
        if (message is null || message.Id.ToString() != eventId || message.Data is null || message.Data.Id == Guid.Empty
            || message.Data.Version is < 1 or > 2
            || message.Type != (message.Data.Version == 1 ? "order.confirmed" : "order.cancelled")
            || message.Data.Status != (message.Data.Version == 1 ? "confirmed" : "cancelled")
            || string.IsNullOrWhiteSpace(message.Data.Reference) || message.Data.Reference.Length > 80
            || message.Data.Sku is not ("LAMP-01" or "DESK-02" or "KIT-03") || message.Data.Quantity is < 1 or > 100
            || (message.Scenario is not null && (message.RunId is null || Experiments.Find(message.Scenario) is null)))
            return Results.BadRequest(new { error = "Invalid order event." });

        var number = await store.BeginRequestAsync(message, token);
        var control = await store.ControlAsync(token);
        var mode = message.RunId is { } runId
            ? await store.RunModeAsync(runId, token) ?? ScenarioMode(message.Scenario, number, runtime.Name)
            : control.Mode;
        if (mode is "reject" or "permanent" or "rate-limit")
        {
            var status = mode == "permanent" ? 422 : mode == "rate-limit" ? 429 : 503;
            var detail = mode == "permanent" ? "Simulated permanent order validation rejection."
                : mode == "rate-limit" ? "Simulated capacity limit; wait before retrying." : "Simulated partner outage.";
            if (mode == "rate-limit") context.Response.Headers.RetryAfter = number == 1 ? "3"
                : DateTimeOffset.UtcNow.AddSeconds(5).ToString("R", CultureInfo.InvariantCulture);
            await store.ObserveAsync(message, "Rejected", detail, status, token);
            return Results.Json(new { error = detail }, statusCode: status);
        }
        if (mode == "offline")
        {
            await store.ObserveAsync(message, "Connection dropped", "Closed without acknowledging or applying the order.", null, token);
            context.Abort();
            return Results.Empty;
        }
        if (mode == "slow")
        {
            try { await Task.Delay(control.DelayMs, token); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
        }
        if (message.Scenario == "burst") await Task.Delay(350, token);
        var result = await store.AcceptAsync(message, body, token);
        if (result == InboxResult.Conflict)
        {
            await store.ObserveAsync(message, "Rejected", "An event or order version was reused with different content.", 409, token);
            return Results.Conflict(new { error = "An event or order version was reused with different content." });
        }
        if (mode == "lost-ack" && result == InboxResult.Accepted)
        {
            await store.ObserveAsync(message, "Reply delayed", "Order already committed; acknowledgement delayed beyond the sender timeout.", null, token);
            await Task.Delay(control.DelayMs, CancellationToken.None);
        }
        return Results.Ok(new ReceiptResult(message.Id, result == InboxResult.Duplicate, result == InboxResult.Stale));
    }

    private static string ScenarioMode(string? scenario, int number, string destination) => scenario switch
    {
        "reject" or "circuit-breaker" => "reject",
        "lost-ack" => "lost-ack",
        "fail-twice" => number <= 2 ? "reject" : "healthy",
        "rate-limit" => number <= 2 ? "rate-limit" : "healthy",
        "permanent" => "permanent",
        "two-partners" when destination == "secondary" => number <= 2 ? "reject" : "healthy",
        _ => "healthy"
    };
}
