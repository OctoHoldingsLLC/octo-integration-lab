namespace Octo.IntegrationLab.Lab;

public sealed record LabOptions(string ConnectionString, Uri PartnerUrl, string SigningSecret,
    string ControlKey, int MaxAttempts, int RetryBaseSeconds, int TimeoutSeconds, int LeaseSeconds)
{
    public static LabOptions Read(IConfiguration config)
    {
        string Required(string name) => config[name] ?? throw new InvalidOperationException($"Set {name} (see compose.yaml).");
        var url = new Uri(Required("PartnerUrl"), UriKind.Absolute);
        var secret = Required("SigningSecret");
        var controlKey = Required("ControlKey");
        if (url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Query.Length > 0
            || url.Fragment.Length > 0 || url.AbsolutePath != "/")
            throw new InvalidOperationException("PartnerUrl must be an HTTP(S) origin.");
        if (secret.Length < 32 || controlKey.Length < 32)
            throw new InvalidOperationException("SigningSecret and ControlKey must each contain at least 32 characters.");
        var attempts = config.GetValue("MaxAttempts", 4);
        var retry = config.GetValue("RetryBaseSeconds", 2);
        var timeout = config.GetValue("TimeoutSeconds", 2);
        var lease = config.GetValue("LeaseSeconds", 15);
        if (attempts is < 1 or > 10 || retry is < 1 or > 30 || timeout is < 1 or > 10 || lease < timeout + 5 || lease > 120)
            throw new InvalidOperationException("Invalid delivery limits: attempts 1–10, retry 1–30s, timeout 1–10s, lease timeout+5 to 120s.");
        return new(Required("ConnectionStrings:Lab"), url, secret, controlKey, attempts, retry, timeout, lease);
    }
}
