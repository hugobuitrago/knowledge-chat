using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Rag.Infrastructure.Observability;

public static class ObservabilityExtensions
{
    public static IHostApplicationBuilder AddRagObservability(
        this IHostApplicationBuilder builder,
        string defaultServiceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);

        builder.Services
            .AddOptions<OpenTelemetryOptions>()
            .BindConfiguration(OpenTelemetryOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                static options => IsValidOptionalAbsoluteUri(options.OtlpEndpoint),
                $"{OpenTelemetryOptions.SectionName}:OtlpEndpoint must be an absolute URI.")
            .ValidateOnStart();

        string serviceName = builder.Configuration[$"{OpenTelemetryOptions.SectionName}:ServiceName"]
            ?? defaultServiceName;
        Uri? exporterEndpoint = ParseOptionalUri(
            builder.Configuration[$"{OpenTelemetryOptions.SectionName}:OtlpEndpoint"]);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(console =>
        {
            console.IncludeScopes = true;
            console.TimestampFormat = "O";
            console.UseUtcTimestamp = true;
        });
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;

            if (exporterEndpoint is not null)
            {
                logging.AddOtlpExporter(options => options.Endpoint = exporterEndpoint);
            }
        });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (exporterEndpoint is not null)
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = exporterEndpoint);
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (exporterEndpoint is not null)
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = exporterEndpoint);
                }
            });

        return builder;
    }

    private static bool IsValidOptionalAbsoluteUri(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        Uri.TryCreate(value, UriKind.Absolute, out _);

    private static Uri? ParseOptionalUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri : null;
}
