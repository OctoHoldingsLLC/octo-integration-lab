using Npgsql;
using Octo.IntegrationLab;
using Octo.IntegrationLab.Lab;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["AllowedHosts"] ??= "localhost;127.0.0.1;lab";
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16 * 1024);
var options = LabOptions.Read(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.ConnectionString));
builder.Services.AddSingleton<LabStore>();
builder.Services.AddSingleton<PartnerClient>();
builder.Services.AddHostedService<DeliveryWorker>();
builder.Services.AddProblemDetails();
foreach (var name in new[] { "partner", "control" })
{
    builder.Services.AddHttpClient(name, client => client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
}
var app = builder.Build();
await app.Services.GetRequiredService<LabStore>().InitializeAsync();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var ownOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if ((origin.Length > 0 && !string.Equals(origin, ownOrigin, StringComparison.OrdinalIgnoreCase))
            || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Use this lab from its own origin." });
            return;
        }
        if (!context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = 415;
            await context.Response.WriteAsJsonAsync(new { error = "Send application/json." });
            return;
        }
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", async (NpgsqlDataSource data, CancellationToken token) =>
{
    await using var command = data.CreateCommand("SELECT 1");
    await command.ExecuteScalarAsync(token);
    return Results.Ok(new { status = "healthy" });
});
app.MapGet("/api/state", (LabStore store, CancellationToken token) => store.StateAsync(token));
app.MapGet("/api/config", () => new { options.MaxAttempts, options.RetryBaseSeconds, options.TimeoutSeconds, options.LeaseSeconds });
app.MapGet("/api/deliveries/{id:guid}", async (Guid id, LabStore store, CancellationToken token) =>
    await store.DetailAsync(id, token) is { } detail ? Results.Ok(detail) : Results.NotFound(new { error = "Event not found." }));
app.MapPost("/api/orders", async (OrderRequest request, HttpRequest http, LabStore store, CancellationToken token) =>
{
    var key = http.Headers["Idempotency-Key"].ToString();
    if (key.Length is < 16 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
        return Results.BadRequest(new { error = "Provide an Idempotency-Key of 16–128 ASCII letters, digits, hyphens, underscores, dots, or colons." });
    if (string.IsNullOrWhiteSpace(request.Reference) || request.Reference.Trim().Length > 80
        || request.Sku is not ("LAMP-01" or "DESK-02" or "KIT-03") || request.Quantity is < 1 or > 100)
        return Results.BadRequest(new { error = "Use a reference (1–80 characters), a listed SKU, and a quantity from 1 to 100." });
    var result = await store.CreateOrderAsync(key, request with { Reference = request.Reference.Trim() }, token);
    if (result.Conflict) return Results.Conflict(new { error = "That idempotency key already belongs to a different order." });
    return Results.Json(new { result.OrderId, result.EventId, result.Reused }, statusCode: result.Reused ? 200 : 201);
});
app.MapPost("/api/worker", async (WorkerControl control, LabStore store, CancellationToken token) =>
{
    await store.SetPausedAsync(control.Paused, token);
    return Results.Ok(control);
});
app.MapPost("/api/deliveries/{id:guid}/replay", async (Guid id, LabStore store, CancellationToken token) =>
    await store.ReplayAsync(id, token) ? Results.Accepted() : Results.Conflict(new { error = "Only delivered or exhausted events can be replayed." }));
app.MapGet("/api/partner", async (PartnerClient partner, CancellationToken token) =>
    await partner.StateAsync(token) is { } state ? Results.Ok(state) : Results.Json(new { error = "Partner service is unreachable." }, statusCode: 503));
app.MapPost("/api/partner", async (PartnerControl control, PartnerClient partner, CancellationToken token) =>
{
    if (!control.IsValid) return Results.BadRequest(new { error = "Unknown mode, or delay outside 3000–15000ms." });
    return await partner.SetAsync(control, token) ? Results.Ok(control) : Results.Json(new { error = "Could not reach the partner control API." }, statusCode: 503);
});
app.Run();

internal sealed record WorkerControl(bool Paused);
