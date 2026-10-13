using Xunit;

namespace Octo.IntegrationLab.Tests;

[Trait("Category", "Unit")]
public sealed class RetryAfterTests
{
    [Theory]
    [InlineData("5", 5)]
    [InlineData("0", 0)]
    [InlineData("99999", 60)]
    [InlineData("9999999999999999999999999999", 60)]
    [InlineData(" 5 ", 5)]
    public void NumericWaitsAreBounded(string value, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), RetryAfter.Parse(value, DateTimeOffset.UtcNow).Delay);

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("nonsense")]
    [InlineData("5,10")]
    public void InvalidWaitFallsBackToLocalPolicy(string value) => Assert.Null(RetryAfter.Parse(value, DateTimeOffset.UtcNow).Delay);

    [Fact]
    public void HttpDateSupportsFuturePastAndLocalBackoffFloor()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        Assert.Equal(TimeSpan.FromSeconds(7), RetryAfter.Parse(now.AddSeconds(7).ToString("R"), now).Delay);
        var past = RetryAfter.Parse(now.AddSeconds(-20).ToString("R"), now);
        Assert.Equal(TimeSpan.Zero, past.Delay);
        Assert.Equal(TimeSpan.FromSeconds(4), RetryAfter.Apply(new("Retrying", TimeSpan.FromSeconds(4)), past).RetryAfter);
        Assert.Null(RetryAfter.Apply(new("Exhausted", null), new(TimeSpan.FromSeconds(60), "")).RetryAfter);
    }

    [Theory]
    [InlineData("Sunday, 06-Nov-94 08:49:37 GMT")]
    [InlineData("Sun Nov  6 08:49:37 1994")]
    public void ObsoleteHttpDatesRemainAccepted(string value)
    {
        var now = new DateTimeOffset(1994, 11, 6, 8, 49, 30, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(7), RetryAfter.Parse(value, now).Delay);
    }
}
