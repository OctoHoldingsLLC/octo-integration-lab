using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Octo.IntegrationLab.Tests;

[Trait("Category", "Integration")]
public sealed class SignatureTests(LabFixture fixture) : IClassFixture<LabFixture>
{
    [Theory]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("wrong-id")]
    [InlineData("malformed")]
    public async Task InvalidSignatureIsRejectedBeforeSideEffects(string mode)
    {
        var id = Guid.NewGuid();
        var body = Body(id);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(mode == "expired" ? -10 : 0).ToUnixTimeSeconds().ToString();
        var signature = WebhookSignature.Sign(fixture.Secret, timestamp, id.ToString(), body);
        if (mode == "tampered") body += " ";
        if (mode == "malformed") signature = "v1=" + new string('z', 64);
        using var request = Signed(body, mode == "wrong-id" ? Guid.NewGuid() : id, timestamp, signature);
        using var response = await fixture.Partner.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM partner_receipts WHERE event_id=$1", id));
    }

    [Fact]
    public async Task ConcurrentValidDeliveriesProcessOnceAndConflictingPayloadIsRejected()
    {
        await fixture.ConfigureAsync("healthy");
        var id = Guid.NewGuid();
        var body = Body(id);
        async Task<bool> SendAsync()
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            using var request = Signed(body, id, timestamp, WebhookSignature.Sign(fixture.Secret, timestamp, id.ToString(), body));
            using var response = await fixture.Partner.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean();
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SendAsync()));
        Assert.Single(results, duplicate => !duplicate);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM partner_orders WHERE event_id=$1", id));
        var changed = body.Replace("LAMP-01", "DESK-02", StringComparison.Ordinal);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var conflictRequest = Signed(changed, id, timestamp, WebhookSignature.Sign(fixture.Secret, timestamp, id.ToString(), changed));
        using var conflict = await fixture.Partner.SendAsync(conflictRequest);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task FirstReleasePayloadWithoutVersionCanStillBeDeliveredAndReplayed()
    {
        await fixture.ConfigureAsync("healthy");
        var id = Guid.NewGuid();
        var body = Body(id).Replace(",\"version\":1", "", StringComparison.Ordinal)
            .Replace(",\"runId\":null,\"scenario\":null", "", StringComparison.Ordinal);
        Assert.DoesNotContain("version", body);
        for (var i = 0; i < 2; i++)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            using var request = Signed(body, id, timestamp, WebhookSignature.Sign(fixture.Secret, timestamp, id.ToString(), body));
            using var response = await fixture.Partner.SendAsync(request);
            response.EnsureSuccessStatusCode();
            Assert.Equal(i == 1, (await response.Content.ReadFromJsonAsync<ReceiptResult>())!.Duplicate);
        }
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM partner_orders WHERE event_id=$1 AND version=1", id));
    }

    [Fact]
    public async Task PartnerControlRequiresItsSeparateKey()
    {
        using var response = await fixture.Partner.PostAsJsonAsync("/api/control", new PartnerControl("offline"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string Body(Guid id) => JsonSerializer.Serialize(new OrderEvent(id, "order.confirmed", DateTimeOffset.UtcNow,
        new(Guid.NewGuid(), "Signed test", "LAMP-01", 1, "confirmed")), LabJson.Options);

    private static HttpRequestMessage Signed(string body, Guid id, string timestamp, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/orders") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.IdHeader, id.ToString());
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp);
        request.Headers.Add(WebhookSignature.SignatureHeader, signature);
        return request;
    }
}
