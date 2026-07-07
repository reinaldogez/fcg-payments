using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.Enums;
using Fcg.Payments.Domain.Exceptions;
using Fcg.Payments.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Fcg.Payments.Tests.Unit.Entities;

public class PagamentoTests
{
    private static Pagamento CriarPagamento() =>
        Pagamento.Criar(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Preco.Criar(100m));

    [Fact]
    public void DeveNascerPendenteSemProcessamento()
    {
        Pagamento pagamento = CriarPagamento();

        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        pagamento.Id.Should().NotBe(Guid.Empty);
        pagamento.CriadoEm.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        pagamento.ProcessadoEm.Should().BeNull();
        pagamento.MotivoRecusa.Should().BeNull();
    }

    [Fact]
    public void DeveAprovarDePendente()
    {
        Pagamento pagamento = CriarPagamento();

        pagamento.Aprovar();

        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.ProcessadoEm.Should().NotBeNull();
        pagamento.ProcessadoEm!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        pagamento.MotivoRecusa.Should().BeNull();
    }

    [Fact]
    public void DeveRejeitarDePendenteComMotivo()
    {
        Pagamento pagamento = CriarPagamento();

        pagamento.Rejeitar("Valor acima do limite autorizado");

        pagamento.Status.Should().Be(StatusPagamento.Rejeitado);
        pagamento.MotivoRecusa.Should().Be("Valor acima do limite autorizado");
        pagamento.ProcessadoEm.Should().NotBeNull();
        pagamento.ProcessadoEm!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DeveRejeitarMotivoVazio()
    {
        Pagamento pagamento = CriarPagamento();

        Action acao = () => pagamento.Rejeitar("   ");

        acao.Should().Throw<DomainException>();
    }

    [Fact]
    public void NaoDeveReprocessarPagamentoAprovado()
    {
        Pagamento pagamento = CriarPagamento();
        pagamento.Aprovar();

        Action aprovarDeNovo = () => pagamento.Aprovar();
        Action rejeitar = () => pagamento.Rejeitar("motivo");

        aprovarDeNovo.Should().Throw<DomainException>();
        rejeitar.Should().Throw<DomainException>();
    }

    [Fact]
    public void NaoDeveReprocessarPagamentoRejeitado()
    {
        Pagamento pagamento = CriarPagamento();
        pagamento.Rejeitar("Valor acima do limite autorizado");

        Action aprovar = () => pagamento.Aprovar();
        Action rejeitarDeNovo = () => pagamento.Rejeitar("outro motivo");

        aprovar.Should().Throw<DomainException>();
        rejeitarDeNovo.Should().Throw<DomainException>();
    }
}
