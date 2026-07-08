using Fcg.Payments.Infrastructure.Persistence;
using Fcg.Payments.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Persistence;

public class SchemaInspectionTests(PaymentsApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task TabelaPagamentosDeveTerColunasEmSnakeCase()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        List<string> tabelas = await db
            .Database.SqlQueryRaw<string>(
                "SELECT table_name::text AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'"
            )
            .ToListAsync();

        tabelas.Should().Contain("pagamentos");

        List<string> colunas = await db
            .Database.SqlQueryRaw<string>(
                "SELECT column_name::text AS \"Value\" FROM information_schema.columns WHERE table_name = 'pagamentos'"
            )
            .ToListAsync();

        colunas
            .Should()
            .Contain([
                "order_id",
                "usuario_id",
                "jogo_id",
                "valor",
                "status",
                "motivo_recusa",
                "criado_em",
                "processado_em",
            ]);
    }

    [Fact]
    public async Task ColunaValorDeveSerNumeric18Escala2()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        int precisao = await db
            .Database.SqlQueryRaw<int>(
                "SELECT numeric_precision AS \"Value\" FROM information_schema.columns WHERE table_name = 'pagamentos' AND column_name = 'valor'"
            )
            .SingleAsync();

        int escala = await db
            .Database.SqlQueryRaw<int>(
                "SELECT numeric_scale AS \"Value\" FROM information_schema.columns WHERE table_name = 'pagamentos' AND column_name = 'valor'"
            )
            .SingleAsync();

        precisao.Should().Be(18);
        escala.Should().Be(2);
    }

    [Fact]
    public async Task ColunaMotivoRecusaDeveSerVarchar500Nullable()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        int tamanhoMaximo = await db
            .Database.SqlQueryRaw<int>(
                "SELECT character_maximum_length AS \"Value\" FROM information_schema.columns WHERE table_name = 'pagamentos' AND column_name = 'motivo_recusa'"
            )
            .SingleAsync();

        string nullable = await db
            .Database.SqlQueryRaw<string>(
                "SELECT is_nullable::text AS \"Value\" FROM information_schema.columns WHERE table_name = 'pagamentos' AND column_name = 'motivo_recusa'"
            )
            .SingleAsync();

        tamanhoMaximo.Should().Be(500);
        nullable.Should().Be("YES");
    }

    [Fact]
    public async Task IndiceDeOrderIdDeveSerUnicoEPleno()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        string definicao = await db
            .Database.SqlQueryRaw<string>(
                "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_pagamentos_order_id'"
            )
            .SingleAsync();

        definicao.Should().Contain("UNIQUE");
        // Índice pleno: um pedido tem no máximo uma decisão de pagamento, em qualquer estado.
        // A ausência de predicado é afirmada explicitamente, não apenas omitida.
        definicao.Should().NotContain("WHERE");
    }

    [Fact]
    public async Task SchemaDeMensageriaDeveExistir()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        List<string> tabelas = await db
            .Database.SqlQueryRaw<string>(
                "SELECT table_name::text AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'"
            )
            .ToListAsync();

        // O wiring de mensageria trouxe as tabelas do Inbox/Outbox transacional (migration própria).
        tabelas.Should().Contain(["inbox_state", "outbox_message", "outbox_state"]);
    }
}
