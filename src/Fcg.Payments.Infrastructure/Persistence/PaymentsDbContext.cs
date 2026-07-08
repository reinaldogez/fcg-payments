using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Fcg.Payments.Infrastructure.Persistence;

// Contexto do serviço. Implementa a porta IUnitOfWork delegando ao SaveChanges do EF —
// o commit é responsabilidade de quem coordena a transação, não do repositório.
public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options)
    : DbContext(options),
        IUnitOfWork
{
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();

    public Task SalvarAlteracoesAsync(CancellationToken ct) => SaveChangesAsync(ct);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Schema de mensageria transacional entra numa migration própria — aqui só o domínio.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
