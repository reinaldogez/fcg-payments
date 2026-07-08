using Fcg.Contracts.Enums;
using Fcg.Contracts.Events;
using Fcg.Payments.Application.UseCases.Pagamentos;
using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.Enums;
using Fcg.Payments.Domain.Interfaces;
using Fcg.Payments.Domain.ValueObjects;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Fcg.Payments.Tests.Unit.UseCases.Pagamentos;

public class ProcessarPagamentoUseCaseTests
{
    private readonly Mock<IPagamentoRepository> _repositorio = new(MockBehavior.Strict);
    private readonly Mock<IGatewayPagamento> _gateway = new(MockBehavior.Strict);
    private readonly Mock<IPublishEndpoint> _publishEndpoint = new(MockBehavior.Strict);
    private readonly Mock<ILogger<ProcessarPagamentoUseCase>> _logger = new();

    private ProcessarPagamentoUseCase CriarUseCase() =>
        new(_repositorio.Object, _gateway.Object, _publishEndpoint.Object, _logger.Object);

    private static OrderPlacedEvent CriarEvento(decimal price = 100m) =>
        new()
        {
            EventVersion = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            UserEmail = "jogador@exemplo.com",
            UserName = "Jogador Exemplo",
            GameId = Guid.NewGuid(),
            GameName = "Jogo Exemplo",
            Price = price,
        };

    // (b) Duplicata de OrderId é no-op: repo já tem uma decisão → nenhum autorizar,
    // adicionar ou publish; apenas um Warning logado.
    [Fact]
    public async Task DuplicataDeOrderIdNaoRepublica()
    {
        OrderPlacedEvent evento = CriarEvento();
        var existente = Pagamento.Criar(
            evento.OrderId,
            evento.UserId,
            evento.GameId,
            Preco.Criar(evento.Price)
        );
        _repositorio
            .Setup(r => r.ObterPorOrderIdAsync(evento.OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existente);

        await CriarUseCase().ExecutarAsync(evento, CancellationToken.None);

        // Único toque no repositório é a consulta de idempotência; nada mais.
        _repositorio.Verify(
            r => r.ObterPorOrderIdAsync(evento.OrderId, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _repositorio.VerifyNoOtherCalls();
        _gateway.VerifyNoOtherCalls();
        _publishEndpoint.VerifyNoOtherCalls();
        VerificarLog(LogLevel.Warning, Times.Once());
    }

    // (c) Ramo aprovado: shape completo do evento de saída, incluindo o trânsito copiado
    // do evento de entrada e o PaymentId originado pelo agregado.
    [Fact]
    public async Task RamoAprovadoPublicaComShapeCorreto()
    {
        OrderPlacedEvent evento = CriarEvento(price: 4200m);
        Pagamento? pagamentoAdicionado = null;
        PaymentProcessedEvent? publicado = null;
        ConfigurarFluxoNovo(evento, ResultadoAutorizacao.Aprovar());
        CapturarAdicionar(p => pagamentoAdicionado = p);
        CapturarPublish(e => publicado = e);

        await CriarUseCase().ExecutarAsync(evento, CancellationToken.None);

        pagamentoAdicionado.Should().NotBeNull();
        pagamentoAdicionado!.Status.Should().Be(StatusPagamento.Aprovado);

        publicado.Should().NotBeNull();
        publicado!.Status.Should().Be(PaymentStatus.Approved);
        publicado.RejectionReason.Should().BeNull();
        publicado.PaymentId.Should().Be(pagamentoAdicionado.Id);
        publicado.OrderId.Should().Be(evento.OrderId);
        publicado.UserId.Should().Be(evento.UserId);
        publicado.GameId.Should().Be(evento.GameId);
        publicado.Price.Should().Be(4200m);
        publicado.EventVersion.Should().Be(1);
        publicado.OccurredAt.Should().Be(pagamentoAdicionado.ProcessadoEm!.Value);

        // Trânsito puro copiado do evento de entrada.
        publicado.UserEmail.Should().Be(evento.UserEmail);
        publicado.UserName.Should().Be(evento.UserName);
        publicado.GameName.Should().Be(evento.GameName);

        VerificarInteracoesExatas(evento);
    }

    // (d) Ramo rejeitado: status Rejected e o motivo do gateway propagado.
    [Fact]
    public async Task RamoRejeitadoPublicaComMotivo()
    {
        const string motivo = "Valor acima do limite autorizado";
        OrderPlacedEvent evento = CriarEvento(price: 9000m);
        Pagamento? pagamentoAdicionado = null;
        PaymentProcessedEvent? publicado = null;
        ConfigurarFluxoNovo(evento, ResultadoAutorizacao.Rejeitar(motivo));
        CapturarAdicionar(p => pagamentoAdicionado = p);
        CapturarPublish(e => publicado = e);

        await CriarUseCase().ExecutarAsync(evento, CancellationToken.None);

        pagamentoAdicionado.Should().NotBeNull();
        pagamentoAdicionado!.Status.Should().Be(StatusPagamento.Rejeitado);

        publicado.Should().NotBeNull();
        publicado!.Status.Should().Be(PaymentStatus.Rejected);
        publicado.RejectionReason.Should().Be(motivo);
        publicado.PaymentId.Should().Be(pagamentoAdicionado.Id);

        VerificarInteracoesExatas(evento);
    }

    // (e) Mapeamento por switch: afirma o membro nomeado do enum de contrato nos dois
    // ramos — nunca comparação por int (a coincidência ordinal não está sendo explorada).
    [Theory]
    [InlineData(false, PaymentStatus.Approved)]
    [InlineData(true, PaymentStatus.Rejected)]
    public async Task MapeamentoDeStatusUsaMembroNomeado(bool rejeitar, PaymentStatus esperado)
    {
        OrderPlacedEvent evento = CriarEvento();
        PaymentProcessedEvent? publicado = null;
        ResultadoAutorizacao resultado = rejeitar
            ? ResultadoAutorizacao.Rejeitar("motivo")
            : ResultadoAutorizacao.Aprovar();
        ConfigurarFluxoNovo(evento, resultado);
        CapturarAdicionar(_ => { });
        CapturarPublish(e => publicado = e);

        await CriarUseCase().ExecutarAsync(evento, CancellationToken.None);

        publicado.Should().NotBeNull();
        publicado!.Status.Should().Be(esperado);
    }

    // Configura o caminho de pagamento novo (sem duplicata) até a decisão do gateway.
    private void ConfigurarFluxoNovo(OrderPlacedEvent evento, ResultadoAutorizacao resultado)
    {
        _repositorio
            .Setup(r => r.ObterPorOrderIdAsync(evento.OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Pagamento?)null);
        _gateway
            .Setup(g =>
                g.AutorizarAsync(
                    evento.OrderId,
                    evento.UserId,
                    evento.Price,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(resultado);
    }

    // Prova (a): no caminho feliz ocorrem exatamente consulta → autorizar → adicionar →
    // publicar, e nada mais (nenhum SaveChanges/transação a mockar porque a dependência
    // sequer existe no caso de uso).
    private void VerificarInteracoesExatas(OrderPlacedEvent evento)
    {
        _repositorio.Verify(
            r => r.ObterPorOrderIdAsync(evento.OrderId, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _repositorio.Verify(
            r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        _gateway.Verify(
            g =>
                g.AutorizarAsync(
                    evento.OrderId,
                    evento.UserId,
                    evento.Price,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _publishEndpoint.Verify(
            p => p.Publish(It.IsAny<PaymentProcessedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        _repositorio.VerifyNoOtherCalls();
        _gateway.VerifyNoOtherCalls();
        _publishEndpoint.VerifyNoOtherCalls();
    }

    private void CapturarAdicionar(Action<Pagamento> captura)
    {
        _repositorio
            .Setup(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()))
            .Callback<Pagamento, CancellationToken>((p, _) => captura(p))
            .Returns(Task.CompletedTask);
    }

    private void CapturarPublish(Action<PaymentProcessedEvent> captura)
    {
        _publishEndpoint
            .Setup(p => p.Publish(It.IsAny<PaymentProcessedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentProcessedEvent, CancellationToken>((e, _) => captura(e))
            .Returns(Task.CompletedTask);
    }

    private void VerificarLog(LogLevel nivel, Times vezes)
    {
        _logger.Verify(
            l =>
                l.Log(
                    nivel,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            vezes
        );
    }
}
