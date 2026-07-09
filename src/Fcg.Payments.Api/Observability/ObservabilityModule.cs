using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Events;
using Serilog.Sinks.Grafana.Loki;

namespace Fcg.Payments.Api.Observability;

public static class ObservabilityModule
{
    private const string ServiceName = "Fcg.Payments.Api";
    private const string AppLabel = "fcg-payments";

    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        // Variavel oficial do OpenTelemetry, lida direta (nao via GetSection); o Loki e push
        // direto do Serilog por Loki:Url. Ambos config-gated: ausencia desliga, presenca liga.
        string? lokiUrl = builder.Configuration["Loki:Url"];
        string? otelEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        // Console e enricher de trace entram sempre; o sink Loki so com URL configurada.
        builder.Host.UseSerilog(
            (_, loggerConfig) =>
            {
                loggerConfig
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .Enrich.WithSpan()
                    .Enrich.WithProperty("service.name", ServiceName)
                    .WriteTo.Console();

                // Label app=fcg-payments como label de stream (nao propriedade) — e o que faz
                // {app="fcg-payments"} e o agregado {app=~"fcg-.*"} resolverem no Grafana.
                if (!string.IsNullOrWhiteSpace(lokiUrl))
                {
                    loggerConfig.WriteTo.GrafanaLoki(
                        lokiUrl,
                        labels: [new LokiLabel { Key = "app", Value = AppLabel }]
                    );
                }
            }
        );

        // OTLP (traces/metrics) so com endpoint configurado. Instrumenta HTTP de entrada
        // (AspNetCore, health) e o bus — MassTransit como source (trace bilateral: encadeia
        // publish e consume pelo TraceId via headers AMQP, o que faz o payments ser o elo do
        // meio da saga) e meter.
        if (!string.IsNullOrWhiteSpace(otelEndpoint))
        {
            builder
                .Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(ServiceName))
                .WithTracing(tracing =>
                    tracing
                        .AddAspNetCoreInstrumentation()
                        .AddSource("MassTransit")
                        .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otelEndpoint))
                )
                .WithMetrics(metrics =>
                    metrics
                        .AddAspNetCoreInstrumentation()
                        .AddMeter("MassTransit")
                        .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otelEndpoint))
                );
        }

        return builder;
    }
}
