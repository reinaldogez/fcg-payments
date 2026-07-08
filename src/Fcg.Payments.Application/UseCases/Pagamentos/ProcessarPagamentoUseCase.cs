using Fcg.Contracts.Enums;
using Fcg.Contracts.Events;
using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.Enums;
using Fcg.Payments.Domain.Interfaces;
using Fcg.Payments.Domain.ValueObjects;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Fcg.Payments.Application.UseCases.Pagamentos;

// Único caso de uso do serviço: decide o pagamento de um pedido e publica o veredito.
// Não comita nem abre transação própria — o commit é do harness do Inbox (transação
// única do contexto scoped ao consumo). Nenhuma unidade de trabalho é injetada de
// propósito: um commit aninhado gravaria parte fora da transação do harness, e o bug
// só apareceria sob redelivery.
public class ProcessarPagamentoUseCase(
    IPagamentoRepository repositorio,
    IGatewayPagamento gateway,
    IPublishEndpoint publishEndpoint,
    ILogger<ProcessarPagamentoUseCase> logger
)
{
    public async Task ExecutarAsync(OrderPlacedEvent evento, CancellationToken ct)
    {
        // Idempotência de negocio: um pedido tem no máximo uma decisão de pagamento, em
        // qualquer estado. Re-publicar geraria novo MessageId → notifications re-enviaria
        // o e-mail. Por isso é no-op silencioso (ACK), sem publish e sem AdicionarAsync.
        Pagamento? existente = await repositorio.ObterPorOrderIdAsync(evento.OrderId, ct);
        if (existente is not null)
        {
            logger.LogWarning(
                "Pagamento já existe para o pedido {OrderId} — ignorando duplicata.",
                evento.OrderId
            );
            return;
        }

        var pagamento = Pagamento.Criar(
            evento.OrderId,
            evento.UserId,
            evento.GameId,
            Preco.Criar(evento.Price)
        );

        ResultadoAutorizacao resultado = await gateway.AutorizarAsync(
            evento.OrderId,
            evento.UserId,
            evento.Price,
            ct
        );

        if (resultado.Aprovado)
            pagamento.Aprovar();
        else
            pagamento.Rejeitar(resultado.Motivo!);

        await repositorio.AdicionarAsync(pagamento, ct);

        // O Publish vira linha de outbox na mesma transação do harness (nunca dual-write).
        // Trânsito puro (UserEmail/UserName/GameName): copiado do evento de entrada, nunca
        // persistido no agregado.
        await publishEndpoint.Publish(
            new PaymentProcessedEvent
            {
                EventVersion = 1,
                OccurredAt = pagamento.ProcessadoEm!.Value,
                PaymentId = pagamento.Id,
                OrderId = pagamento.OrderId,
                UserId = pagamento.UsuarioId,
                UserEmail = evento.UserEmail,
                UserName = evento.UserName,
                GameId = pagamento.JogoId,
                GameName = evento.GameName,
                Price = pagamento.Valor.Valor,
                Status = MapearStatus(pagamento.Status),
                RejectionReason = pagamento.MotivoRecusa,
            },
            ct
        );

        if (resultado.Aprovado)
        {
            logger.LogInformation(
                "Pagamento {PaymentId} aprovado para o pedido {OrderId}.",
                pagamento.Id,
                pagamento.OrderId
            );
        }
        else
        {
            logger.LogInformation(
                "Pagamento {PaymentId} rejeitado para o pedido {OrderId}.",
                pagamento.Id,
                pagamento.OrderId
            );
        }
    }

    // Switch explícito: a coincidência ordinal entre StatusPagamento e PaymentStatus
    // (1/1, 2/2) é acidente — nunca um cast. Pendente/default é estado impossível neste
    // ponto (o pagamento já está terminal) e lançar deixa o bug barulhento se algum dia
    // ocorrer.
    private static PaymentStatus MapearStatus(StatusPagamento status) =>
        status switch
        {
            StatusPagamento.Aprovado => PaymentStatus.Approved,
            StatusPagamento.Rejeitado => PaymentStatus.Rejected,
            _ => throw new InvalidOperationException(
                $"Pagamento em estado {status} não é publicável — esperado terminal."
            ),
        };
}
