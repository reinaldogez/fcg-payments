using Fcg.Payments.Domain.Entities;
using Fcg.Payments.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fcg.Payments.Infrastructure.Persistence.Configs;

public class PagamentoConfig : IEntityTypeConfiguration<Pagamento>
{
    public void Configure(EntityTypeBuilder<Pagamento> builder)
    {
        builder.HasKey(p => p.Id);

        // VO Preco ↔ decimal. Leitura via Reconstituir (sem revalidar — o valor já foi
        // validado quando entrou no banco).
        builder
            .Property(p => p.Valor)
            .HasConversion(v => v.Valor, valor => Preco.Reconstituir(valor))
            .HasColumnType("numeric(18,2)")
            .IsRequired();

        builder.Property(p => p.MotivoRecusa).HasMaxLength(500);

        // Índice pleno (sem filtro): um pedido tem no máximo uma decisão de pagamento,
        // em qualquer estado. É a rede de segurança da corrida entre réplicas — a segunda
        // insersão do mesmo OrderId estoura a constraint e cai no ramo no-op na retentativa.
        builder.HasIndex(p => p.OrderId).IsUnique().HasDatabaseName("ux_pagamentos_order_id");
    }
}
