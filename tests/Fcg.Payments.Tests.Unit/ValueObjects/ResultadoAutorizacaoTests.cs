using Fcg.Payments.Domain.Exceptions;
using Fcg.Payments.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Fcg.Payments.Tests.Unit.ValueObjects;

public class ResultadoAutorizacaoTests
{
    [Fact]
    public void AprovarDeveResultarAprovadoSemMotivo()
    {
        var resultado = ResultadoAutorizacao.Aprovar();

        resultado.Aprovado.Should().BeTrue();
        resultado.Motivo.Should().BeNull();
    }

    [Fact]
    public void RejeitarDeveResultarNaoAprovadoComMotivo()
    {
        var resultado = ResultadoAutorizacao.Rejeitar("Valor acima do limite");

        resultado.Aprovado.Should().BeFalse();
        resultado.Motivo.Should().Be("Valor acima do limite");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejeitarSemMotivoDeveLancar(string? motivo)
    {
        Action acao = () => ResultadoAutorizacao.Rejeitar(motivo!);

        acao.Should().Throw<DomainException>();
    }
}
