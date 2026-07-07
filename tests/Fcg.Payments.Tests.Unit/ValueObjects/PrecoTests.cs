using Fcg.Payments.Domain.Exceptions;
using Fcg.Payments.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Fcg.Payments.Tests.Unit.ValueObjects;

public class PrecoTests
{
    [Fact]
    public void DeveRejeitarPrecoNegativo()
    {
        Action acao = () => Preco.Criar(-0.01m);

        acao.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(49.9)]
    [InlineData(5000)]
    public void DeveCriarPrecoNaoNegativo(decimal valor)
    {
        var preco = Preco.Criar(valor);

        preco.Valor.Should().Be(valor);
    }

    [Fact]
    public void DeveReconstituirSemValidar()
    {
        var preco = Preco.Reconstituir(-10m);

        preco.Valor.Should().Be(-10m);
    }

    [Fact]
    public void DeveCompararPorValor() => Preco.Criar(100m).Should().Be(Preco.Criar(100m));
}
