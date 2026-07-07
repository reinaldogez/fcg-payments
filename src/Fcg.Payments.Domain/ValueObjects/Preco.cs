using Fcg.Payments.Domain.Exceptions;

namespace Fcg.Payments.Domain.ValueObjects;

// Valor monetário em BRL implícito. Snapshot do valor do veredito de pagamento.
public record Preco
{
    private Preco(decimal valor) => Valor = valor;

    public decimal Valor { get; }

    public static Preco Criar(decimal valor)
    {
        if (valor < 0)
            throw new DomainException("O preço não pode ser negativo.");

        return new Preco(valor);
    }

    // Materialização (leitura do EF) — sem validação.
    public static Preco Reconstituir(decimal valor) => new(valor);
}
