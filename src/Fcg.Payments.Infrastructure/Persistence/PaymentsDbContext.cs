using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.Interfaces;
using MassTransit;
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
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);

        // Outbox transacional (a linha do evento cai na mesma transação do agregado no publish)
        // + Inbox ativo (dedup do consumo por MessageId) — este serviço consome e publica.
        modelBuilder.AddTransactionalOutboxEntities();
        modelBuilder.AddInboxStateEntity();

        base.OnModelCreating(modelBuilder);
    }
}
