using System.Text.Json;
using Npgsql;
using Octo.IntegrationLab;
using Octo.IntegrationLab.Partner;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16 * 1024);
var secret = builder.Configuration["SigningSecret"] ?? throw new InvalidOperationException("Set SigningSecret.");
var controlKey = builder.Configuration["ControlKey"] ?? throw new InvalidOperationException("Set ControlKey.");
if (secret.Length < 32 || controlKey.Length < 32) throw new InvalidOperationException("Use secrets of at least 32 characters.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Lab")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Lab.")));
builder.Services.AddSingleton<PartnerStore>();
builder.Services.AddProblemDetails();
var app = builder.Build();
await app.Services.GetRequiredService<PartnerStore>().InitializeAsync();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (!WebhookSignature.SecretEquals(controlKey, context.Request.Headers["X-Lab-Control-Key"].ToString()))
        {
            context.Response.StatusCode = 401;
            return;
        }
        if (HttpMethods.IsPost(context.Request.Method) && !context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = 415;
            return;
        }
    }
    await next(context);
});
app.MapGet("/health", async (NpgsqlDataSource data, CancellationToken token) =>
{
    await using var command = data.CreateCommand("SELECT 1");
    await command.ExecuteScalarAsync(token);
    return Results.Ok(new { status = "healthy" });
});
app.MapGet("/api/control", (PartnerStore store, CancellationToken token) => store.StateAsync(token));
app.MapPost("/api/control", async (PartnerControl control, PartnerStore store, CancellationToken token) =>
{
    if (!control.IsValid) return Results.BadRequest(new { error = "Invalid partner mode or delay." });
    await store.SetControlAsync(control, token);
    return Results.Ok(control);
});
app.MapPost("/webhooks/orders", async (HttpContext context, PartnerStore store) =>
{
    if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync(context.RequestAborted);
    var eventId = context.Request.Headers[WebhookSignature.IdHeader].ToString();
    if (!WebhookSignature.Verify(secret, context.Request.Headers[WebhookSignature.TimestampHeader].ToString(),
        eventId, body, context.Request.Headers[WebhookSignature.SignatureHeader].ToString(), DateTimeOffset.UtcNow))
        return Results.Json(new { error = "Invalid or expired signature." }, statusCode: 401);
    OrderEvent? message;
    try { message = JsonSerializer.Deserialize<OrderEvent>(body, LabJson.Options); }
    catch (JsonException) { return Results.BadRequest(new { error = "Invalid event JSON." }); }
    if (message is null || message.Id.ToString() != eventId || message.Type != "order.confirmed"
        || message.Data is null || message.Data.Id == Guid.Empty || message.Data.Status != "confirmed"
        || string.IsNullOrWhiteSpace(message.Data.Reference) || message.Data.Reference.Length > 80
        || message.Data.Sku is not ("LAMP-01" or "DESK-02" or "KIT-03") || message.Data.Quantity is < 1 or > 100)
        return Results.BadRequest(new { error = "Invalid order event." });
    var control = await store.ControlAsync(context.RequestAborted);
    if (control.Mode == "reject") return Results.Json(new { error = "Simulated partner outage." }, statusCode: 503);
    if (control.Mode == "offline")
    {
        context.Abort();
        return Results.Empty;
    }
    if (control.Mode == "slow")
    {
        try { await Task.Delay(control.DelayMs, context.RequestAborted); }
        catch (OperationCanceledException) { return Results.StatusCode(499); }
    }
    var result = await store.AcceptAsync(message, body, context.RequestAborted);
    if (result == InboxResult.Conflict) return Results.Conflict(new { error = "An event or order identifier was reused with different content." });
    if (control.Mode == "lost-ack" && result == InboxResult.Accepted)
    {
        // Deliberately finish the side effect before delaying the response beyond the sender's timeout.
        await Task.Delay(control.DelayMs, CancellationToken.None);
    }
    return Results.Ok(new ReceiptResult(message.Id, result == InboxResult.Duplicate));
});
app.Run();
