using Fcg.Payments.Tests.Integration.Fixtures;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Messaging;

// Os hosted services do MassTransit são removidos na fixture (o sweeper do Outbox dá deadlock
// com o reset de banco), então o bus não sobe sozinho. Aqui ele é iniciado sob demanda: iniciar
// declara a topologia no RabbitMQ real, e um canal AMQP direto inspeciona o resultado — provando
// o nome literal da fila (a armadilha do kebab formatter, que geraria order-placed sem sufixo) e
// o tipo fanout da exchange, sem depender da Management API.
[Collection(IntegrationCollection.Name)]
public class TopologiaBusTests(PaymentsApiFactory factory)
{
    [Fact]
    public async Task DeveDeclararFilaLiteralEExchangeFanout()
    {
        IBusControl bus = factory.Services.GetRequiredService<IBusControl>();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await bus.StartAsync(cts.Token);
        try
        {
            ConnectionFactory conexao = new() { Uri = new Uri(factory.RabbitMqConnectionString) };
            await using IConnection connection = await conexao.CreateConnectionAsync(cts.Token);

            // Fila com o nome literal: o declare passivo falha se a fila com esse nome exato não
            // existir. É a prova anti-kebab — order-placed.fcg-payments, com o sufixo do repo.
            await using (
                IChannel canal = await connection.CreateChannelAsync(cancellationToken: cts.Token)
            )
            {
                Func<Task> declararFila = () =>
                    canal.QueueDeclarePassiveAsync("order-placed.fcg-payments", cts.Token);
                await declararFila.Should().NotThrowAsync();
            }

            // Cobertura negativa: a fila sem o sufixo (o que o kebab formatter geraria) não existe.
            await using (
                IChannel canal = await connection.CreateChannelAsync(cancellationToken: cts.Token)
            )
            {
                Func<Task> declararSemSufixo = () =>
                    canal.QueueDeclarePassiveAsync("order-placed", cts.Token);
                await declararSemSufixo
                    .Should()
                    .ThrowAsync<OperationInterruptedException>(
                        "o declare passivo de uma fila inexistente é recusado pelo broker"
                    );
            }

            // Exchange fanout: re-declarar com o mesmo tipo é no-op; se o tipo real divergisse, o
            // broker responderia PRECONDITION_FAILED (406). Afirma o tipo sem Management API.
            await using (
                IChannel canal = await connection.CreateChannelAsync(cancellationToken: cts.Token)
            )
            {
                Func<Task> declararFanout = () =>
                    canal.ExchangeDeclareAsync(
                        "payment-processed",
                        ExchangeType.Fanout,
                        durable: true,
                        autoDelete: false,
                        cancellationToken: cts.Token
                    );
                await declararFanout.Should().NotThrowAsync();
            }
        }
        finally
        {
            await bus.StopAsync(cts.Token);
        }
    }
}
