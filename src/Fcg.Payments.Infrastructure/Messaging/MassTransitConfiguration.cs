using Fcg.Contracts.Events;
using Fcg.Payments.Infrastructure.Consumers;
using Fcg.Payments.Infrastructure.Persistence;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fcg.Payments.Infrastructure.Messaging;

public static class MassTransitConfiguration
{
    public static IServiceCollection AddPaymentsMessaging(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddMassTransit(x =>
        {
            // Outbox transacional sobre o mesmo PaymentsDbContext: a linha do evento cai na
            // transação do agregado (publish) e o Inbox deduplica entregas repetidas (consume).
            x.AddEntityFrameworkOutbox<PaymentsDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            x.AddConsumer<OrderPlacedConsumer>();

            x.UsingRabbitMq(
                (context, cfg) =>
                {
                    // Host por campos separados (Host/Port não-sensível via ConfigMap;
                    // Username/Password via Secret) — fail-fast se faltar o essencial.
                    string host =
                        configuration["RabbitMq:Host"]
                        ?? throw new InvalidOperationException("RabbitMq:Host não configurado.");
                    string username =
                        configuration["RabbitMq:Username"]
                        ?? throw new InvalidOperationException(
                            "RabbitMq:Username não configurado."
                        );
                    string password =
                        configuration["RabbitMq:Password"]
                        ?? throw new InvalidOperationException(
                            "RabbitMq:Password não configurado."
                        );
                    ushort port = ushort.TryParse(configuration["RabbitMq:Port"], out ushort p)
                        ? p
                        : (ushort)5672;

                    cfg.Host(
                        host,
                        port,
                        "/",
                        h =>
                        {
                            h.Username(username);
                            h.Password(password);
                        }
                    );

                    // Nome de exchange/fila vive no bus, não no contrato (Fcg.Contracts são
                    // records puros, sem [EntityName]).
                    // Publish: payment-processed (fanout) — o payments é o publicador.
                    cfg.Message<PaymentProcessedEvent>(m => m.SetEntityName("payment-processed"));
                    cfg.Publish<PaymentProcessedEvent>(p => p.ExchangeType = "fanout");

                    // Consume: bind da fila na exchange order-placed (publicada pelo catalog).
                    cfg.Message<OrderPlacedEvent>(m => m.SetEntityName("order-placed"));

                    // ReceiveEndpoint explícito (não kebab formatter): entrega o sufixo
                    // .fcg-payments da fila consumidora — o formatter derivaria order-placed sem
                    // o sufixo, quebrando a convenção de nomes silenciosamente.
                    cfg.ReceiveEndpoint(
                        "order-placed.fcg-payments",
                        e =>
                        {
                            // Inbox no endpoint: deduplica a mesma mensagem (mesmo MessageId) sob
                            // redelivery e envolve as escritas do consumer numa transação única do
                            // mesmo PaymentsDbContext scoped — o commit é do harness, não do use case.
                            e.UseEntityFrameworkOutbox<PaymentsDbContext>(context);
                            e.ConfigureConsumer<OrderPlacedConsumer>(context);
                        }
                    );
                }
            );
        });

        // O check do bus do MassTransit nasce com a tag "ready". Removê-la: o readiness fica
        // só-Postgres — o Outbox desacopla a entrega do broker, então broker fora não deve
        // derrubar a prontidão (o veredito ainda fica seguro na Outbox).
        services.PostConfigure<HealthCheckServiceOptions>(options =>
        {
            foreach (
                HealthCheckRegistration registro in options.Registrations.Where(r =>
                    r.Name.StartsWith("masstransit", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                registro.Tags.Remove("ready");
            }
        });

        return services;
    }
}
