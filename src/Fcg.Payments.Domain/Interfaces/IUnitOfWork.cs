namespace Fcg.Payments.Domain.Interfaces;

public interface IUnitOfWork
{
    Task SalvarAlteracoesAsync(CancellationToken ct);
}
