using Fcg.Payments.Domain.Entities;

namespace Fcg.Payments.Domain.Interfaces;

public interface IPagamentoRepository
{
    Task<Pagamento?> ObterPorOrderIdAsync(Guid orderId, CancellationToken ct);

    Task AdicionarAsync(Pagamento pagamento, CancellationToken ct);
}
