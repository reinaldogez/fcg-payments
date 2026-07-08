using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fcg.Payments.Api.Health;

public static class HealthCheckExtensions
{
    // /health/live  — liveness: só o self (sem dependências); falha → k8s reinicia o pod.
    // /health/ready — readiness: só PostgreSQL (tag "ready"); falha → k8s tira do balanceamento.
    //                 O broker fica fora do ready: o Outbox desacopla a entrega do broker.
    // /health       — agregado informativo de todos os checks registrados.
    public static IEndpointRouteBuilder MapPaymentsHealthChecks(
        this IEndpointRouteBuilder endpoints
    )
    {
        endpoints.MapHealthChecks(
            "/health/live",
            new HealthCheckOptions { Predicate = _ => false }
        );

        endpoints.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready"),
                ResponseWriter = EscreverRespostaAsync,
            }
        );

        endpoints.MapHealthChecks(
            "/health",
            new HealthCheckOptions { ResponseWriter = EscreverRespostaAsync }
        );

        return endpoints;
    }

    private static Task EscreverRespostaAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        string corpo = JsonSerializer.Serialize(
            new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                }),
            }
        );

        return context.Response.WriteAsync(corpo);
    }
}
