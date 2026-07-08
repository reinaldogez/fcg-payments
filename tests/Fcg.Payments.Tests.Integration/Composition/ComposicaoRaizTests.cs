using Fcg.Payments.Application.Options;
using Fcg.Payments.Application.UseCases.Pagamentos;
using Fcg.Payments.Domain.Interfaces;
using Fcg.Payments.Infrastructure.Persistence;
using Fcg.Payments.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Composition;

[Collection(IntegrationCollection.Name)]
public class ComposicaoRaizTests(PaymentsApiFactory factory)
{
    // Rede de segurança da composição-raiz: valida o grafo de DI de produção (AddApplication +
    // AddInfrastructure), como composto pelo host, sem supridores de teste. Se qualquer serviço
    // consumido pelo use case não tiver registro, a resolução aqui lança e o teste falha.
    [Fact]
    public void HostDeveResolverOUseCaseSuasDependenciasEAsOptions()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        Action resolver = () =>
        {
            sp.GetRequiredService<ProcessarPagamentoUseCase>();
            sp.GetRequiredService<IPagamentoRepository>();
            sp.GetRequiredService<IGatewayPagamento>();
        };

        resolver.Should().NotThrow();

        // Nada sobrepõe a seção Payment na fixture: o default determinístico embutido vale.
        IOptions<PaymentSettings> options = sp.GetRequiredService<IOptions<PaymentSettings>>();
        options.Value.RejectionThreshold.Should().Be(5000m);
    }

    [Fact]
    public void UnitOfWorkEDbContextDevemSerAMesmaInstanciaNoMesmoScope()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        IUnitOfWork unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        PaymentsDbContext contexto = sp.GetRequiredService<PaymentsDbContext>();

        // Alicerce da transação única do harness de consumo: a mesma instância scoped do
        // contexto responde por IUnitOfWork e por PaymentsDbContext.
        ReferenceEquals(unitOfWork, contexto).Should().BeTrue();
    }
}

// Fora da coleção de Integration: sobe um host próprio (sem Postgres) só para provar o fail-fast.
public class ComposicaoFailFastTests
{
    [Fact]
    public void StartupSemConnectionStringDeveFalharNoStart()
    {
        using WebApplicationFactory<Program> factory =
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("ConnectionStrings:Payments", "")
            );

        // CreateClient dispara o start do host — e com ele o fail-fast da connection string.
        Action start = () => factory.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("Payments");
    }
}
