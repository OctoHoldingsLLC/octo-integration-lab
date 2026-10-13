namespace Octo.IntegrationLab.Lab;

public static class ExperimentEndpoints
{
    public static void MapExperimentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/experiments", () => Experiments.All);
        app.MapGet("/api/runs", (LabStore store, CancellationToken token) => store.RunsAsync(token));
        app.MapGet("/api/runs/{id:guid}", async (Guid id, ExperimentService service, CancellationToken token) =>
            await service.ReadAsync(id, token) is { } run ? Results.Ok(run) : Results.NotFound());
        app.MapPost("/api/runs", async (ExperimentRequest request, HttpRequest http, LabStore store, LabOptions options, CancellationToken token) =>
        {
            var key = http.Headers["Idempotency-Key"].ToString();
            var definition = Experiments.Find(request.Scenario);
            if (key.Length is < 16 or > 128 || definition is null || request.BatchSize is < 1 or > 50
                || (request.BatchSize is not null && request.Scenario != "burst"))
                return Results.BadRequest(new { error = "Choose a listed experiment, provide an idempotency key, and use 1–50 orders for a burst." });
            if (request.Scenario == "two-partners" && options.SecondaryPartnerUrl is null)
                return Results.BadRequest(new { error = "Start the second partner using the supplied Compose configuration." });
            var result = await store.CreateRunAsync(key, request, token);
            if (result.Conflict) return Results.Conflict(new { error = "That request key belongs to a different experiment." });
            return Results.Json(result, statusCode: result.Reused ? 200 : 201);
        });
        app.MapPost("/api/runs/{id:guid}/resume", async (Guid id, LabStore store, CancellationToken token) =>
            await store.SetRunPausedAsync(id, false, token) ? Results.Ok() : Results.NotFound());
        app.MapPost("/api/runs/{id:guid}/recover", async (Guid id, ExperimentService service, CancellationToken token) =>
            await service.RecoverAsync(id, token) ? Results.Ok() : Results.Json(new { error = "Could not restore every destination. It is safe to retry." }, statusCode: 503));
    }
}
