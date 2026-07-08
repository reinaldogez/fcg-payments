using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fcg.Payments.Infrastructure.Persistence;

// Factory de design-time para o `dotnet ef` (migrations) funcionar sem host.
// Não abre conexão — UseNpgsql sem connection string basta para o EF construir o
// modelo; nenhuma credencial vive em código.
public class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<PaymentsDbContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql().UseSnakeCaseNamingConvention();

        return new PaymentsDbContext(optionsBuilder.Options);
    }
}
