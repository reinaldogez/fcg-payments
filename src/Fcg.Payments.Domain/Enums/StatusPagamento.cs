namespace Fcg.Payments.Domain.Enums;

// Persistido como int ordinal. Valores explícitos e append-only:
// reordenar ou reusar um valor já atribuído corrompe silenciosamente
// as linhas históricas sob armazenamento ordinal.
public enum StatusPagamento
{
    Pendente = 0,
    Aprovado = 1,
    Rejeitado = 2,
    // Append-only: novos membros SEMPRE com valor novo explícito.
    // NUNCA reordenar nem reusar um valor já atribuído.
}
