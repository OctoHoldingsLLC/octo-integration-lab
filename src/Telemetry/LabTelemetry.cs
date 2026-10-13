using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Octo.IntegrationLab.Telemetry;

public static class LabTelemetry
{
    public const string SourceName = "Octo.IntegrationLab";
    public static readonly ActivitySource Source = new(SourceName);

    public static IServiceCollection AddLabTelemetry(this IServiceCollection services, IConfiguration configuration, string service)
    {
        services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService(service))
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new AlwaysOnSampler()).AddSource(SourceName)
                    .AddAspNetCoreInstrumentation(options => options.Filter = context => context.Request.Method == "POST")
                    .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = request =>
                        request.RequestUri?.AbsolutePath == "/webhooks/orders");
                if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                    tracing.AddOtlpExporter();
            });
        return services;
    }
}
