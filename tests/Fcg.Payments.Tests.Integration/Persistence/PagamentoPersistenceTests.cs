using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.Enums;
using Fcg.Payments.Domain.Interfaces;
using Fcg.Payments.Domain.ValueObjects;
using Fcg.Payments.Tests.Integration.Fixtures;
using FluentAssertions;
using FluentAssertions.Specialized;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Persistence;

public class PagamentoPersistenceTests(PaymentsApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task DevePreservarCamposEVoAoPersistirERelerPagamentoAprovado()
    {
        var orderId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var jogoId = Guid.NewGuid();

        var pagamento = Pagamento.Criar(orderId, usuarioId, jogoId, Preco.Criar(199.99m));
        pagamento.Aprovar();
        Guid id = pagamento.Id;

        await PersistirAsync(pagamento);

        await using AsyncServiceScope leitura = Factory.Services.CreateAsyncScope();
        IPagamentoRepository repositorio =
            leitura.ServiceProvider.GetRequiredService<IPagamentoRepository>();

        Pagamento? lido = await repositorio.ObterPorOrderIdAsync(orderId, CancellationToken.None);

        lido.Should().NotBeNull();
        lido!.Id.Should().Be(id);
        lido.OrderId.Should().Be(orderId);
        lido.UsuarioId.Should().Be(usuarioId);
        lido.JogoId.Should().Be(jogoId);
        // VO Preco materializado via Reconstituir preserva o valor.
        lido.Valor.Valor.Should().Be(199.99m);
        lido.Status.Should().Be(StatusPagamento.Aprovado);
        lido.ProcessadoEm.Should().NotBeNull();
        lido.MotivoRecusa.Should().BeNull();
    }

    [Fact]
    public async Task DevePreservarMotivoRecusaAoPersistirERelerPagamentoRejeitado()
    {
        var orderId = Guid.NewGuid();

        var pagamento = Pagamento.Criar(
            orderId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Preco.Criar(6000m)
        );
        pagamento.Rejeitar("Valor acima do limite autorizado");

        await PersistirAsync(pagamento);

        await using AsyncServiceScope leitura = Factory.Services.CreateAsyncScope();
        IPagamentoRepository repositorio =
            leitura.ServiceProvider.GetRequiredService<IPagamentoRepository>();

        Pagamento? lido = await repositorio.ObterPorOrderIdAsync(orderId, CancellationToken.None);

        lido.Should().NotBeNull();
        lido!.Status.Should().Be(StatusPagamento.Rejeitado);
        lido.MotivoRecusa.Should().Be("Valor acima do limite autorizado");
        lido.ProcessadoEm.Should().NotBeNull();
    }

    [Fact]
    public async Task DeveBarrarSegundoPagamentoParaOMesmoOrderId()
    {
        var orderId = Guid.NewGuid();

        // Dois agregados distintos (Id gerado internamente por Criar) para o mesmo pedido.
        await PersistirAsync(
            Pagamento.Criar(orderId, Guid.NewGuid(), Guid.NewGuid(), Preco.Criar(50m))
        );

        Func<Task> segundo = () =>
            PersistirAsync(
                Pagamento.Criar(orderId, Guid.NewGuid(), Guid.NewGuid(), Preco.Criar(50m))
            );

        // O índice único pleno ux_pagamentos_order_id é a rede de segurança da corrida entre
        // réplicas: a segunda inserção do mesmo OrderId estoura a constraint no banco real.
        ExceptionAssertions<PostgresException> excecao = await segundo
            .Should()
            .ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>();

        excecao.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    private async Task PersistirAsync(Pagamento pagamento)
    {
        await using AsyncServiceScope escrita = Factory.Services.CreateAsyncScope();
        IPagamentoRepository repositorio =
            escrita.ServiceProvider.GetRequiredService<IPagamentoRepository>();
        IUnitOfWork unitOfWork = escrita.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repositorio.AdicionarAsync(pagamento, CancellationToken.None);
        await unitOfWork.SalvarAlteracoesAsync(CancellationToken.None);
    }
}
