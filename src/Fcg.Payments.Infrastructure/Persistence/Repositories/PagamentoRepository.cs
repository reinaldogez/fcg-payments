using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Fcg.Payments.Infrastructure.Persistence.Repositories;

public class PagamentoRepository(PaymentsDbContext contexto) : IPagamentoRepository
{
    // OrderId não é a PK — consulta por predicado, não FindAsync.
    public Task<Pagamento?> ObterPorOrderIdAsync(Guid orderId, CancellationToken ct) =>
        contexto.Pagamentos.FirstOrDefaultAsync(p => p.OrderId == orderId, ct);

    // Só rastreia a inserção — o commit é de quem coordena a transação, não do repositório.
    public Task AdicionarAsync(Pagamento pagamento, CancellationToken ct) =>
        contexto.Pagamentos.AddAsync(pagamento, ct).AsTask();
}
