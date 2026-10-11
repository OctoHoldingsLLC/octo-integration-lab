using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Octo.IntegrationLab.Lab;

public sealed class DeliveryWorker(LabStore store, LabOptions options, IHttpClientFactory clients,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        => await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RunAsync(stoppingToken)));

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.RecoverExpiredAsync(options.MaxAttempts, stoppingToken);
                var claim = await store.ClaimAsync(options.LeaseSeconds, stoppingToken);
                if (claim is null)
                {
                    await Task.Delay(250, stoppingToken);
                    continue;
                }
                await DispatchAsync(claim, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Dispatcher could not complete its cycle; persisted leases will recover unfinished attempts.");
                try { await Task.Delay(1000, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task DispatchAsync(DeliveryClaim claim, CancellationToken stoppingToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.PartnerUrl, "webhooks/orders"));
        request.Headers.Add(WebhookSignature.IdHeader, claim.Id.ToString());
        request.Headers.Add("Idempotency-Key", claim.Id.ToString());
        request.Headers.Add("X-Octo-Attempt", claim.Attempt.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp);
        request.Headers.Add(WebhookSignature.SignatureHeader,
            WebhookSignature.Sign(options.SigningSecret, timestamp, claim.Id.ToString(), claim.Payload));
        request.Content = new StringContent(claim.Payload, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var watch = Stopwatch.StartNew();
        int? status = null;
        var success = false;
        var outcome = "Failed";
        string detail;
        try
        {
            using var response = await clients.CreateClient("partner").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            success = response.IsSuccessStatusCode;
            detail = $"Receiver returned HTTP {status}.";
            if (success)
            {
                // HTTP 2xx is the acknowledgement. Response JSON is only bounded, optional diagnostics.
                var duplicate = await WasDuplicateAsync(response, claim.Id, timeout.Token);
                outcome = duplicate ? "Duplicate" : "Accepted";
                detail = duplicate ? "Already received. The order was not processed again." : "Signature verified; order and receipt committed by the receiver.";
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            outcome = "Timeout";
            detail = $"No acknowledgement within {options.TimeoutSeconds}s. The receiver may still have accepted the event.";
        }
        catch (HttpRequestException)
        {
            outcome = "NetworkError";
            detail = "Connection failed or was dropped. The receiver's outcome is unknown.";
        }
        var decision = DeliveryPolicy.Decide(success, status, claim.CycleAttempt, options.MaxAttempts,
            options.RetryBaseSeconds, Random.Shared.NextDouble() * 0.25);
        await store.CompleteAsync(claim, decision, outcome, status, (int)watch.ElapsedMilliseconds, detail, stoppingToken);
        logger.LogInformation("Event {EventId} attempt {Attempt}: {Outcome}; delivery {Status}",
            claim.Id, claim.Attempt, outcome, decision.Status);
    }

    private static async Task<bool> WasDuplicateAsync(HttpResponseMessage response, Guid id, CancellationToken token)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);
            var buffer = new char[1025];
            var count = await reader.ReadBlockAsync(buffer, token);
            if (count > 1024) return false;
            var receipt = JsonSerializer.Deserialize<ReceiptResult>(buffer.AsSpan(0, count), LabJson.Options);
            return receipt is { Duplicate: true } && receipt.EventId == id;
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}
