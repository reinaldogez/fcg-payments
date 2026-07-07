namespace Fcg.Payments.Domain.Exceptions;

// Erro de regra de negócio. Lançada no consumo — o pipeline de mensageria
// (retry/DLQ) decide o destino; não há superfície HTTP para traduzir.
// Nunca usar exceções genéricas (ArgumentException etc.) para erro de domínio.
public class DomainException(string mensagem) : Exception(mensagem) { }
