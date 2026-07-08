using Fcg.Payments.Domain.Interfaces;
using Fcg.Payments.Infrastructure.Gateways;
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
        services.AddScoped<IGatewayPagamento, GatewayPagamentoSimulado>();
        return services;
    }
}
