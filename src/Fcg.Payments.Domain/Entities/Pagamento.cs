using Fcg.Payments.Domain.Enums;
using Fcg.Payments.Domain.Exceptions;
using Fcg.Payments.Domain.ValueObjects;

namespace Fcg.Payments.Domain.Entities;

// A decisão de pagamento de um pedido. Nasce Pendente e transiciona uma única
// vez para um estado terminal (Aprovado/Rejeitado), imutável a partir daí.
// O Id é o PaymentId do veredito — este serviço origina esse identificador.
public class Pagamento
{
    // EF materializa por aqui.
    private Pagamento() { }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UsuarioId { get; private set; }
    public Guid JogoId { get; private set; }
    public Preco Valor { get; private set; } = null!;
    public StatusPagamento Status { get; private set; }
    public string? MotivoRecusa { get; private set; }
    public DateTime CriadoEm { get; private set; }
    public DateTime? ProcessadoEm { get; private set; }

    public static Pagamento Criar(Guid orderId, Guid usuarioId, Guid jogoId, Preco valor)
    {
        return new Pagamento
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            UsuarioId = usuarioId,
            JogoId = jogoId,
            Valor = valor,
            Status = StatusPagamento.Pendente,
            CriadoEm = DateTime.UtcNow,
        };
    }

    public void Aprovar()
    {
        GarantirPendente();

        Status = StatusPagamento.Aprovado;
        ProcessadoEm = DateTime.UtcNow;
    }

    public void Rejeitar(string motivo)
    {
        GarantirPendente();

        if (string.IsNullOrWhiteSpace(motivo))
            throw new DomainException("Uma rejeição exige um motivo.");

        Status = StatusPagamento.Rejeitado;
        MotivoRecusa = motivo;
        ProcessadoEm = DateTime.UtcNow;
    }

    private void GarantirPendente()
    {
        if (Status != StatusPagamento.Pendente)
            throw new DomainException(
                $"Um pagamento em estado {Status} não pode ser processado novamente."
            );
    }
}
