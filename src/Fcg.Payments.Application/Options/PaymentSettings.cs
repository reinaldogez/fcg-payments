namespace Fcg.Payments.Application.Options;

public class PaymentSettings
{
    public const string SectionName = "Payment";

    // Limite de autorização da simulação. Default determinístico e inofensivo —
    // config sobrepõe (Payment:RejectionThreshold); sem validador/fail-fast.
    public decimal RejectionThreshold { get; set; } = 5000m;
}
