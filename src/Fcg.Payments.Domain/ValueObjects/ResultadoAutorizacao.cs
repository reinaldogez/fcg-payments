using Fcg.Payments.Domain.Exceptions;

namespace Fcg.Payments.Domain.ValueObjects;

// Resultado da porta do gateway de pagamento. A invariante "motivo sse
// rejeitado" é garantida no tipo: o ctor é privado e o único caminho de
// construção são as factories, então estado inválido é inconstruível de fora.
public record ResultadoAutorizacao
{
    private ResultadoAutorizacao(bool aprovado, string? motivo)
    {
        Aprovado = aprovado;
        Motivo = motivo;
    }

    public bool Aprovado { get; }

    public string? Motivo { get; }

    public static ResultadoAutorizacao Aprovar() => new(aprovado: true, motivo: null);

    public static ResultadoAutorizacao Rejeitar(string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo))
            throw new DomainException("Uma rejeição exige um motivo.");

        return new ResultadoAutorizacao(aprovado: false, motivo);
    }
}
