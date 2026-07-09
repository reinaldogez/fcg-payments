using Fcg.Payments.Api.Health;
using Fcg.Payments.Api.Observability;
using Fcg.Payments.Application;
using Fcg.Payments.Application.Options;
using Fcg.Payments.Infrastructure;
using Fcg.Payments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Observabilidade config-gated (console sempre; Loki/OTLP só com env). Entra antes do
// check do Job para que o modo --migrate também emita log estruturado no console.
builder.AddObservability();

// Fail-fast: o serviço depende de PostgreSQL; sem connection string não há boot válido
// (nem para o Job de migração).
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Payments")))
    throw new InvalidOperationException("Connection string 'Payments' não configurada.");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Threshold do gateway simulado. O POCO tem default determinístico embutido; config
// sobrepõe (Payment:RejectionThreshold) — sem validador/fail-fast.
builder.Services.Configure<PaymentSettings>(
    builder.Configuration.GetSection(PaymentSettings.SectionName)
);

builder
    .Services.AddHealthChecks()
    .AddDbContextCheck<PaymentsDbContext>("postgres", tags: ["ready"]);

WebApplication app = builder.Build();

// Modo Job: o mesmo binário aplica migrations e encerra sem subir o host web.
// Roda antes de qualquer pipeline; o boot normal nunca migra.
if (args.Contains("--migrate"))
{
    using IServiceScope scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
    return;
}

app.MapPaymentsHealthChecks();

app.Run();
