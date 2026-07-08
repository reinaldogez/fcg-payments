using Fcg.Payments.Application.UseCases.Pagamentos;
using Microsoft.Extensions.DependencyInjection;

namespace Fcg.Payments.Application;

public static class DependencyInjection
{
    // Scoped: o caso de uso vive no scope da mensagem (mesma instância do contexto scoped
    // ao consumo, quando o wiring de mensageria for ligado).
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ProcessarPagamentoUseCase>();
        return services;
    }
}
