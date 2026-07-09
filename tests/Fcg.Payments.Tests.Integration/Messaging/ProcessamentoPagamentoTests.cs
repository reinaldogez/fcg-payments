using System.Text.Json;
using Fcg.Contracts.Enums;
using Fcg.Contracts.Events;
using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.Enums;
using Fcg.Payments.Domain.Interfaces;
using Fcg.Payments.Infrastructure.Persistence;
using Fcg.Payments.Tests.Integration.Fixtures;
using Fcg.Payments.Tests.Integration.Persistence;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Messaging;

// As quatro provas de comportamento do serviço — o critério central. Exercitam o caminho real
// de mensageria de ponta a ponta: publicar OrderPlacedEvent no broker, deixar o consumer real
// (Inbox + Outbox transacional) processar e afirmar o veredito.
//
// Mecânica central: os hosted services do MassTransit são removidos na fixture (o sweeper do
// Outbox dá deadlock com o reset de banco entre testes). Publicar o evento de entrada por um
// IPublishEndpoint scoped o mandaria à outbox local (UseBusOutbox) e ele nunca chegaria ao
// broker; por isso o evento de entrada vai pelo IBusControl, iniciado sob demanda — o publish
// direto do bus não passa pela outbox, e StartAsync também liga o receive endpoint que consome.
//
// Ponto de observação do veredito: o broker, não a outbox_message. A entrega da outbox do
// consume é parte do pipeline do receive endpoint (pré-ACK), não do sweeper de fundo removido —
// então a linha da outbox é entregue à exchange payment-processed e apagada logo após o commit,
// não fica retida. Uma sonda AMQP de teste (fila efêmera bound na fanout payment-processed)
// captura a entrega real — garantia mais forte que a linha local: prova que o evento saiu de
// fato. O sinal de "processado" continua vindo do banco (pagamentos/inbox_state persistem).
public class ProcessamentoPagamentoTests(PaymentsApiFactory factory) : IntegrationTestBase(factory)
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    // Janela de espera negativa: após o sinal de processamento (banco), o tempo que se espera
    // por uma segunda mensagem na sonda antes de concluir que ela não veio. Determinístico o
    // suficiente porque o processamento já terminou quando esta janela abre.
    private static readonly TimeSpan s_janelaEntrega = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task DeveProcessarPagamentoAprovadoDeFormaAtomica()
    {
        OrderPlacedEvent evento = CriarEvento(price: 100m);

        List<EventoProcessado> entregues = await ComSondaAsync(
            async (bus, sonda, ct) =>
            {
                await PublicarAsync(bus, evento, messageId: null, ct);
                await EsperarContagemPagamentosAsync(1, ct);
                return await DrenarSondaAsync(sonda, evento.OrderId);
            }
        );

        // Uma linha em pagamentos, já em estado terminal Aprovado (Pendente nunca comita
        // observável — nasce e transiciona na mesma transação do consumo).
        Pagamento? pagamento = await ObterPagamentoAsync(evento.OrderId);
        pagamento.Should().NotBeNull();
        pagamento!.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.MotivoRecusa.Should().BeNull();

        // A marca do Inbox persiste (sem cleanup) — a terceira escrita da transação única.
        (await ContarInboxAsync())
            .Should()
            .Be(1);

        // Exatamente um PaymentProcessedEvent entregue de verdade na exchange, com o shape do
        // veredito. Status como membro nomeado do contrato (nunca o literal 1/2).
        entregues.Should().ContainSingle();
        EventoProcessado publicado = entregues[0];
        publicado.PaymentId.Should().Be(pagamento.Id);
        publicado.Status.Should().Be((int)PaymentStatus.Approved);
        publicado.RejectionReason.Should().BeNull();
        // Trânsito puro: copiado do evento de entrada, nunca persistido no agregado.
        publicado.UserEmail.Should().Be(evento.UserEmail);
        publicado.UserName.Should().Be(evento.UserName);
        publicado.GameName.Should().Be(evento.GameName);
    }

    [Fact]
    public async Task DeveProcessarPagamentoRejeitadoDeFormaAtomica()
    {
        // 5000.01 ultrapassa o threshold de 5000 (estrito) — o ramo de rejeição.
        OrderPlacedEvent evento = CriarEvento(price: 5000.01m);

        List<EventoProcessado> entregues = await ComSondaAsync(
            async (bus, sonda, ct) =>
            {
                await PublicarAsync(bus, evento, messageId: null, ct);
                await EsperarContagemPagamentosAsync(1, ct);
                return await DrenarSondaAsync(sonda, evento.OrderId);
            }
        );

        Pagamento? pagamento = await ObterPagamentoAsync(evento.OrderId);
        pagamento.Should().NotBeNull();
        pagamento!.Status.Should().Be(StatusPagamento.Rejeitado);
        pagamento.MotivoRecusa.Should().Be("Valor acima do limite autorizado");

        (await ContarInboxAsync()).Should().Be(1);

        entregues.Should().ContainSingle();
        EventoProcessado publicado = entregues[0];
        publicado.PaymentId.Should().Be(pagamento.Id);
        publicado.Status.Should().Be((int)PaymentStatus.Rejected);
        publicado.RejectionReason.Should().Be("Valor acima do limite autorizado");
    }

    [Fact]
    public async Task RedeliveryDaMesmaMensagemNaoDuplicaNemRepublica()
    {
        // Mesma mensagem, mesmo MessageId, entregue duas vezes. O Inbox (dedup por MessageId)
        // processa a primeira e trata a segunda como no-op — uma linha, uma entrega.
        var messageId = Guid.NewGuid();
        OrderPlacedEvent evento = CriarEvento(price: 100m);

        List<EventoProcessado> entregues = await ComSondaAsync(
            async (bus, sonda, ct) =>
            {
                await PublicarAsync(bus, evento, messageId, ct);
                await PublicarAsync(bus, evento, messageId, ct);
                // Sinal de processamento: receive_count chega a 2 quando ambas as entregas do
                // mesmo MessageId foram processadas pelo harness (a segunda, dedupada).
                await EsperarInboxRecebimentosAsync(messageId, 2, ct);
                return await DrenarSondaAsync(sonda, evento.OrderId);
            }
        );

        (await ContarPagamentosAsync()).Should().Be(1);
        entregues.Should().ContainSingle();
    }

    [Fact]
    public async Task DuplicataDeNegocioNaoDuplicaNemRepublica()
    {
        // MessageId distintos (duas mensagens diferentes para o Inbox) com o MESMO OrderId.
        // O Inbox não barra este caso — quem barra é a idempotência de negócio no use case
        // (consulta prévia ObterPorOrderIdAsync + índice único ux_pagamentos_order_id).
        var orderId = Guid.NewGuid();
        OrderPlacedEvent primeiro = CriarEvento(price: 100m, orderId: orderId);
        OrderPlacedEvent segundo = CriarEvento(price: 100m, orderId: orderId);

        List<EventoProcessado> entregues = await ComSondaAsync(
            async (bus, sonda, ct) =>
            {
                await PublicarAsync(bus, primeiro, Guid.NewGuid(), ct);
                await PublicarAsync(bus, segundo, Guid.NewGuid(), ct);
                // Sinal de processamento: ambas consumidas (duas linhas de Inbox — MessageId
                // distintos; o Inbox não deduplicou nada).
                await EsperarContagemInboxAsync(2, ct);
                return await DrenarSondaAsync(sonda, orderId);
            }
        );

        (await ContarPagamentosAsync()).Should().Be(1);
        entregues.Should().ContainSingle();
        // A cereja: as duas mensagens foram de fato consumidas; a dedup aqui foi da camada de
        // negócio, não do Inbox.
        (await ContarInboxAsync())
            .Should()
            .Be(2);
    }

    [Fact]
    public async Task DevePublicarExatamenteUmEventoPorOrderId()
    {
        // A garantia de saída, afirmada diretamente sobre a entrega real: sob duas mensagens
        // (MessageId distintos) para o mesmo OrderId, exatamente um PaymentProcessedEvent sai
        // na exchange. É a linha de defesa nomeada contra regressões futuras no fluxo.
        var orderId = Guid.NewGuid();
        OrderPlacedEvent primeiro = CriarEvento(price: 100m, orderId: orderId);
        OrderPlacedEvent segundo = CriarEvento(price: 100m, orderId: orderId);

        List<EventoProcessado> entregues = await ComSondaAsync(
            async (bus, sonda, ct) =>
            {
                await PublicarAsync(bus, primeiro, Guid.NewGuid(), ct);
                await PublicarAsync(bus, segundo, Guid.NewGuid(), ct);
                await EsperarContagemInboxAsync(2, ct);
                return await DrenarSondaAsync(sonda, orderId);
            }
        );

        entregues
            .Should()
            .ContainSingle("exatamente um PaymentProcessedEvent deve sair por OrderId");
    }

    // Inicia o bus sob demanda (a fixture removeu os hosted services), abre um canal AMQP direto
    // no mesmo broker, declara uma sonda efêmera bound na fanout payment-processed antes de
    // qualquer publish, executa o corpo e sempre para o bus/fecha a conexão ao fim. A sonda
    // captura a entrega real do veredito na exchange.
    private async Task<T> ComSondaAsync<T>(
        Func<IBusControl, Sonda, CancellationToken, Task<T>> corpo
    )
    {
        IBusControl bus = Factory.Services.GetRequiredService<IBusControl>();
        using var cts = new CancellationTokenSource(s_timeout);

        // StartAsync declara a topologia (inclusive a exchange payment-processed) e liga o
        // receive endpoint consumidor.
        await bus.StartAsync(cts.Token);
        try
        {
            var conexao = new ConnectionFactory { Uri = new Uri(Factory.RabbitMqConnectionString) };
            await using IConnection connection = await conexao.CreateConnectionAsync(cts.Token);
            await using IChannel canal = await connection.CreateChannelAsync(
                cancellationToken: cts.Token
            );

            // Idempotente: re-declarar a fanout com o mesmo tipo é no-op (o bus já a declarou).
            await canal.ExchangeDeclareAsync(
                "payment-processed",
                ExchangeType.Fanout,
                durable: true,
                autoDelete: false,
                cancellationToken: cts.Token
            );

            // Fila server-named, exclusive e auto-delete: some quando a conexão fecha, sem
            // resíduo entre testes. Bound na fanout antes de publicar a entrada.
            QueueDeclareOk fila = await canal.QueueDeclareAsync(
                queue: string.Empty,
                durable: false,
                exclusive: true,
                autoDelete: true,
                arguments: null,
                cancellationToken: cts.Token
            );
            await canal.QueueBindAsync(
                fila.QueueName,
                "payment-processed",
                routingKey: string.Empty,
                cancellationToken: cts.Token
            );

            return await corpo(bus, new Sonda(canal, fila.QueueName), cts.Token);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    // Shape autoritativo do evento de entrada (espelha o construído no teste do use case).
    private static OrderPlacedEvent CriarEvento(decimal price, Guid? orderId = null) =>
        new()
        {
            EventVersion = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId ?? Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            UserEmail = "jogador@exemplo.com",
            UserName = "Jogador Exemplo",
            GameId = Guid.NewGuid(),
            GameName = "Jogo Exemplo",
            Price = price,
        };

    // Publish direto do bus (não passa pela outbox local). O MessageId é controlado quando a
    // prova exige: mesmo valor = redelivery da mesma mensagem; valores distintos = mensagens
    // diferentes para o Inbox.
    private static Task PublicarAsync(
        IBusControl bus,
        OrderPlacedEvent evento,
        Guid? messageId,
        CancellationToken ct
    ) =>
        bus.Publish(
            evento,
            ctx =>
            {
                if (messageId is Guid mid)
                    ctx.MessageId = mid;
            },
            ct
        );

    // Drena a sonda até esgotar as mensagens do OrderId, com uma janela de espera limitada após
    // o sinal de processamento (o processamento já terminou quando esta drenagem começa, então
    // a ausência de mais mensagens após a janela é conclusiva). Parse estruturado do envelope
    // do MassTransit — a mensagem vive sob "message", em camelCase.
    private static async Task<List<EventoProcessado>> DrenarSondaAsync(Sonda sonda, Guid orderId)
    {
        var eventos = new List<EventoProcessado>();
        using var janela = new CancellationTokenSource(s_janelaEntrega);

        while (true)
        {
            BasicGetResult? resultado = await sonda.Canal.BasicGetAsync(
                sonda.Fila,
                autoAck: true,
                cancellationToken: CancellationToken.None
            );

            if (resultado is null)
            {
                if (janela.IsCancellationRequested)
                    break;

                await Task.Delay(50, CancellationToken.None);
                continue;
            }

            string corpo = System.Text.Encoding.UTF8.GetString(resultado.Body.Span);
            using var doc = JsonDocument.Parse(corpo);

            // Confere o tipo do contrato no envelope antes de ler o payload.
            JsonElement tipos = doc.RootElement.GetProperty("messageType");
            bool ehPagamento = tipos
                .EnumerateArray()
                .Any(t =>
                    t.GetString() == "urn:message:Fcg.Contracts.Events:PaymentProcessedEvent"
                );
            if (!ehPagamento)
                continue;

            JsonElement message = doc.RootElement.GetProperty("message");
            if (Guid.Parse(message.GetProperty("orderId").GetString()!) != orderId)
                continue;

            eventos.Add(
                new EventoProcessado(
                    PaymentId: Guid.Parse(message.GetProperty("paymentId").GetString()!),
                    // O enum serializa como int ordinal — afirmado contra (int)PaymentStatus.*.
                    Status: message.GetProperty("status").GetInt32(),
                    RejectionReason: LerOpcional(message, "rejectionReason"),
                    UserEmail: message.GetProperty("userEmail").GetString()!,
                    UserName: message.GetProperty("userName").GetString()!,
                    GameName: message.GetProperty("gameName").GetString()!
                )
            );
        }

        return eventos;
    }

    private static string? LerOpcional(JsonElement message, string propriedade) =>
        message.TryGetProperty(propriedade, out JsonElement valor)
        && valor.ValueKind != JsonValueKind.Null
            ? valor.GetString()
            : null;

    private Task EsperarContagemPagamentosAsync(int alvo, CancellationToken ct) =>
        EsperarAsync(
            ContarPagamentosAsync,
            alvo,
            $"a contagem de pagamentos não atingiu {alvo}",
            ct
        );

    private Task EsperarContagemInboxAsync(int alvo, CancellationToken ct) =>
        EsperarAsync(ContarInboxAsync, alvo, $"a contagem de inbox_state não atingiu {alvo}", ct);

    private Task EsperarInboxRecebimentosAsync(Guid messageId, int alvo, CancellationToken ct) =>
        EsperarAsync(
            () => ContarRecebimentosInboxAsync(messageId),
            alvo,
            $"receive_count da mensagem {messageId} não atingiu {alvo}",
            ct
        );

    // Polling do banco até o alvo (>=) ou timeout — nunca Task.Delay fixo como sinal. Falha com
    // mensagem clara nomeando a condição não atingida.
    private static async Task EsperarAsync(
        Func<Task<int>> consultar,
        int alvo,
        string mensagemFalha,
        CancellationToken ct
    )
    {
        while (true)
        {
            if (await consultar() >= alvo)
                return;

            if (ct.IsCancellationRequested)
                throw new TimeoutException($"Timeout esperando o processamento: {mensagemFalha}.");

            await Task.Delay(100, ct);
        }
    }

    private Task<int> ContarPagamentosAsync() =>
        EscalarAsync("SELECT count(*)::int AS \"Value\" FROM pagamentos");

    private Task<int> ContarInboxAsync() =>
        EscalarAsync("SELECT count(*)::int AS \"Value\" FROM inbox_state");

    private async Task<int> ContarRecebimentosInboxAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        // Sem linha de Inbox ainda, retorna vazio → 0; o polling continua até aparecer.
        List<int> valores = await db
            .Database.SqlQueryRaw<int>(
                "SELECT COALESCE(receive_count, 0)::int AS \"Value\" FROM inbox_state WHERE message_id = {0}",
                messageId
            )
            .ToListAsync();

        return valores.Count == 0 ? 0 : valores[0];
    }

    private async Task<int> EscalarAsync(string sql)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }

    private async Task<Pagamento?> ObterPagamentoAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IPagamentoRepository repositorio =
            scope.ServiceProvider.GetRequiredService<IPagamentoRepository>();
        return await repositorio.ObterPorOrderIdAsync(orderId, CancellationToken.None);
    }

    private sealed record EventoProcessado(
        Guid PaymentId,
        int Status,
        string? RejectionReason,
        string UserEmail,
        string UserName,
        string GameName
    );

    // Sonda AMQP: o canal direto e o nome da fila efêmera bound na exchange payment-processed.
    private sealed record Sonda(IChannel Canal, string Fila);
}
