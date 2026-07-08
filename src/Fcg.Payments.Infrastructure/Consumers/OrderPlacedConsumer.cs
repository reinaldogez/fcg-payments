using Fcg.Contracts.Events;
using Fcg.Payments.Application.UseCases.Pagamentos;
using MassTransit;

namespace Fcg.Payments.Infrastructure.Consumers;

// Humble Object da borda de mensageria, degenerado em despacho direto: ao contrário do catalog
// (que ramifica por Status para dois use cases), aqui a decisão aprovar/rejeitar nasce dentro do
// fluxo (resultado do gateway), então a casca não tem informação para ramificar — só despacha.
// Não comita nem trata idempotência à mão: o Inbox (UseEntityFrameworkOutbox no endpoint)
// deduplica a redelivery por MessageId e o harness comita as escritas numa transação única.
public class OrderPlacedConsumer(ProcessarPagamentoUseCase processar) : IConsumer<OrderPlacedEvent>
{
    public Task Consume(ConsumeContext<OrderPlacedEvent> context) =>
        processar.ExecutarAsync(context.Message, context.CancellationToken);
}
