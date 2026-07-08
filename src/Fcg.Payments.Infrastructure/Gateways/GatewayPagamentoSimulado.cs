using Fcg.Payments.Application.Options;
using Fcg.Payments.Domain.Interfaces;
using Fcg.Payments.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace Fcg.Payments.Infrastructure.Gateways;

// Adapter simulado da porta de autorização. Determinístico e em-processo (sem I/O,
// sem latência, sem aleatoriedade) — é essa pureza que legitima chamá-lo dentro da
// transação do harness do Inbox. orderId/userId fazem parte da porta (o gateway real
// os usaria), mas não participam da regra da simulação.
public class GatewayPagamentoSimulado(IOptions<PaymentSettings> options) : IGatewayPagamento
{
    public Task<ResultadoAutorizacao> AutorizarAsync(
        Guid orderId,
        Guid userId,
        decimal price,
        CancellationToken ct
    )
    {
        decimal threshold = options.Value.RejectionThreshold;

        // Estrito: preço igual ao limite aprova; só ultrapassá-lo rejeita.
        ResultadoAutorizacao resultado =
            price > threshold
                ? ResultadoAutorizacao.Rejeitar("Valor acima do limite autorizado")
                : ResultadoAutorizacao.Aprovar();

        return Task.FromResult(resultado);
    }
}
