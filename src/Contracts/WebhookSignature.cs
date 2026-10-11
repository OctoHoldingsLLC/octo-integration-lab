using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Octo.IntegrationLab;

public static class WebhookSignature
{
    public const string IdHeader = "X-Octo-Event-Id";
    public const string TimestampHeader = "X-Octo-Timestamp";
    public const string SignatureHeader = "X-Octo-Signature";

    public static string Sign(string secret, string timestamp, string eventId, string body)
    {
        var bytes = Encoding.UTF8.GetBytes($"{timestamp}.{eventId}.{body}");
        return "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes));
    }

    public static bool Verify(string secret, string timestamp, string eventId, string body,
        string signature, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !Guid.TryParseExact(eventId, "D", out _)
            || signature.Length != 67 || !signature.StartsWith("v1=", StringComparison.Ordinal)) return false;
        // Compare seconds before converting to a DateTimeOffset, including out-of-range input.
        if (Math.Abs((decimal)now.ToUnixTimeSeconds() - seconds) > 300) return false;
        if (!signature[3..].All(Uri.IsHexDigit)) return false;
        var supplied = Convert.FromHexString(signature[3..]);
        var expected = Convert.FromHexString(Sign(secret, timestamp, eventId, body).AsSpan(3));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public static bool SecretEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
