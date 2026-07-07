using Fcg.Payments.Domain.ValueObjects;

namespace Fcg.Payments.Domain.Interfaces;

// Porta de autorização de pagamento. Assinatura de primitivos — é o contrato
// que a evolução para um gateway HTTP real preserva.
public interface IGatewayPagamento
{
    Task<ResultadoAutorizacao> AutorizarAsync(
        Guid orderId,
        Guid userId,
        decimal price,
        CancellationToken ct
    );
}
