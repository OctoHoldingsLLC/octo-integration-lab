using Xunit;

namespace Octo.IntegrationLab.Tests;

[Trait("Category", "Unit")]
public sealed class UnitTests
{
    private const string Secret = "test-signing-secret-at-least-32-characters";

    [Fact]
    public void SignatureBindsBodyIdTimestampAndSecret()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var id = Guid.NewGuid().ToString();
        const string body = "{\"quantity\":2}";
        var signature = WebhookSignature.Sign(Secret, timestamp, id, body);
        Assert.True(WebhookSignature.Verify(Secret, timestamp, id, body, signature, now));
        Assert.False(WebhookSignature.Verify(Secret, timestamp, id, body + " ", signature, now));
        Assert.False(WebhookSignature.Verify(Secret, timestamp, Guid.NewGuid().ToString(), body, signature, now));
        Assert.False(WebhookSignature.Verify(Secret + "x", timestamp, id, body, signature, now));
        Assert.False(WebhookSignature.Verify(Secret, timestamp, id, body, signature, now.AddMinutes(6)));
        Assert.False(WebhookSignature.Verify(Secret, timestamp, id, body, signature, now.AddMinutes(-6)));
    }

    [Theory]
    [InlineData("9223372036854775807", "v1=invalid")]
    [InlineData("-9223372036854775808", "v1=invalid")]
    [InlineData("not-a-timestamp", "v1=invalid")]
    [InlineData("0", "")]
    public void MalformedSignaturesAreRejectedWithoutThrowing(string timestamp, string signature) =>
        Assert.False(WebhookSignature.Verify(Secret, timestamp, Guid.NewGuid().ToString(), "{}", signature, DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(null)]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void TransientFailureRetriesWithBoundedBackoff(int? status)
    {
        Assert.Equal(new DeliveryDecision("Retrying", TimeSpan.FromSeconds(2)), DeliveryPolicy.Decide(false, status, 1, 4, 2, 0));
        Assert.Equal(new DeliveryDecision("Retrying", TimeSpan.FromSeconds(5)), DeliveryPolicy.Decide(false, status, 2, 4, 2, .25));
        Assert.Equal("Exhausted", DeliveryPolicy.Decide(false, status, 4, 4, 2, 0).Status);
        Assert.Equal(TimeSpan.FromSeconds(60), DeliveryPolicy.Decide(false, status, 9, 10, 30, .25).RetryAfter);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(302)]
    public void PermanentFailureDoesNotRetry(int status) =>
        Assert.Equal("Exhausted", DeliveryPolicy.Decide(false, status, 1, 4, 2, 0).Status);

    [Fact]
    public void AcknowledgementCompletesEvenLastAttempt() =>
        Assert.Equal(new DeliveryDecision("Delivered", null), DeliveryPolicy.Decide(true, 200, 4, 4, 2, 0));
}
