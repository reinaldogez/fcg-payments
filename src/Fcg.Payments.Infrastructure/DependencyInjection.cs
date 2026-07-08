using Fcg.Payments.Domain.Interfaces;
using Fcg.Payments.Infrastructure.Gateways;
using Fcg.Payments.Infrastructure.Messaging;
using Fcg.Payments.Infrastructure.Persistence;
using Fcg.Payments.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fcg.Payments.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        string? cs = configuration.GetConnectionString("Payments");
        services.AddDbContext<PaymentsDbContext>(options =>
        {
            // Boot normal (health) não precisa de banco: sem connection string o provider
            // é registrado seco, sem conectar. A migração é ato explícito à parte.
            if (string.IsNullOrWhiteSpace(cs))
                options.UseNpgsql().UseSnakeCaseNamingConvention();
            else
                options.UseNpgsql(cs).UseSnakeCaseNamingConvention();
        });

        services.AddScoped<IPagamentoRepository, PagamentoRepository>();

        // A MESMA instância scoped do contexto responde por IUnitOfWork — é o alicerce da
        // transação única compartilhada por repositório e commit no fluxo de consumo.
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<PaymentsDbContext>());

        services.AddScoped<IGatewayPagamento, GatewayPagamentoSimulado>();

        services.AddPaymentsMessaging(configuration);
        return services;
    }
}
