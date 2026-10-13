using Npgsql;
using Octo.IntegrationLab;
using Octo.IntegrationLab.Partner;
using Octo.IntegrationLab.Telemetry;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLabTelemetry(builder.Configuration, "octo-partner-" + (builder.Configuration["PartnerName"] ?? "primary"));
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16 * 1024);
var secret = builder.Configuration["SigningSecret"] ?? throw new InvalidOperationException("Set SigningSecret.");
var controlKey = builder.Configuration["ControlKey"] ?? throw new InvalidOperationException("Set ControlKey.");
if (secret.Length < 32 || controlKey.Length < 32) throw new InvalidOperationException("Use secrets of at least 32 characters.");
var connectionString = builder.Configuration.GetConnectionString("Lab") ?? throw new InvalidOperationException("Set ConnectionStrings__Lab.");
var schema = builder.Configuration["StoreSchema"] ?? "public";
if (schema is not ("public" or "partner_secondary")) throw new InvalidOperationException("Unknown partner schema.");
if (schema == "partner_secondary")
{
    await using var setup = NpgsqlDataSource.Create(connectionString);
    await using var create = setup.CreateCommand("CREATE SCHEMA IF NOT EXISTS partner_secondary");
    await create.ExecuteNonQueryAsync();
}
var dataOptions = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(dataOptions.ConnectionString));
builder.Services.AddSingleton(new PartnerRuntime(secret, builder.Configuration["PartnerName"] ?? "primary"));
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
app.MapGet("/api/events/{id:guid}", (Guid id, PartnerStore store, CancellationToken token) => store.EventAsync(id, token));
app.MapGet("/api/runs/{id:guid}", (Guid id, PartnerStore store, CancellationToken token) => store.RunAsync(id, token));
app.MapPost("/api/runs/{id:guid}", async (Guid id, RunControl control, PartnerStore store, CancellationToken token) =>
{
    if (control.Mode is not ("healthy" or "reject")) return Results.BadRequest();
    await store.SetRunModeAsync(id, control.Mode, token);
    return Results.Ok(control);
});
app.MapPost("/api/control", async (PartnerControl control, PartnerStore store, CancellationToken token) =>
{
    if (!control.IsValid) return Results.BadRequest(new { error = "Invalid partner mode or delay." });
    await store.SetControlAsync(control, token);
    return Results.Ok(control);
});
app.MapPost("/webhooks/orders", WebhookEndpoint.HandleAsync);
app.Run();
