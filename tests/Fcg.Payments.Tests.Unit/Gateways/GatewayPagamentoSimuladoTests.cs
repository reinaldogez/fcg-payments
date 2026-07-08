using Fcg.Payments.Application.Options;
using Fcg.Payments.Domain.ValueObjects;
using Fcg.Payments.Infrastructure.Gateways;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fcg.Payments.Tests.Unit.Gateways;

public class GatewayPagamentoSimuladoTests
{
    private static GatewayPagamentoSimulado CriarGateway(decimal? threshold = null)
    {
        var settings = new PaymentSettings();
        if (threshold is not null)
            settings.RejectionThreshold = threshold.Value;

        return new GatewayPagamentoSimulado(Options.Create(settings));
    }

    [Fact]
    public async Task PrecoAbaixoDoLimiteDeveAprovar()
    {
        GatewayPagamentoSimulado gateway = CriarGateway();

        ResultadoAutorizacao resultado = await gateway.AutorizarAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            100m,
            CancellationToken.None
        );

        resultado.Aprovado.Should().BeTrue();
        resultado.Motivo.Should().BeNull();
    }

    [Fact]
    public async Task PrecoAcimaDoLimiteDeveRejeitarComMotivoLiteral()
    {
        GatewayPagamentoSimulado gateway = CriarGateway();

        ResultadoAutorizacao resultado = await gateway.AutorizarAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            9999.99m,
            CancellationToken.None
        );

        resultado.Aprovado.Should().BeFalse();
        resultado.Motivo.Should().Be("Valor acima do limite autorizado");
    }

    [Fact]
    public async Task PrecoExatamenteNoLimiteDeveAprovar()
    {
        GatewayPagamentoSimulado gateway = CriarGateway();

        ResultadoAutorizacao resultado = await gateway.AutorizarAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            5000m,
            CancellationToken.None
        );

        resultado.Aprovado.Should().BeTrue();
        resultado.Motivo.Should().BeNull();
    }

    [Fact]
    public async Task PrecoUmCentavoAcimaDoLimiteDeveRejeitar()
    {
        GatewayPagamentoSimulado gateway = CriarGateway();

        ResultadoAutorizacao resultado = await gateway.AutorizarAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            5000.01m,
            CancellationToken.None
        );

        resultado.Aprovado.Should().BeFalse();
        resultado.Motivo.Should().Be("Valor acima do limite autorizado");
    }

    [Fact]
    public async Task ThresholdVemDoOptionsNaoDeConstante()
    {
        GatewayPagamentoSimulado gateway = CriarGateway(threshold: 100m);

        ResultadoAutorizacao acima = await gateway.AutorizarAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            150m,
            CancellationToken.None
        );
        ResultadoAutorizacao noLimite = await gateway.AutorizarAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            100m,
            CancellationToken.None
        );

        acima.Aprovado.Should().BeFalse();
        noLimite.Aprovado.Should().BeTrue();
    }
}
