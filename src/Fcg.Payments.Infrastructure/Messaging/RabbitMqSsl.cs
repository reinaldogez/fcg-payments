using Microsoft.Extensions.Configuration;

namespace Fcg.Payments.Infrastructure.Messaging;

// Leitura isolada da chave que habilita TLS no host do bus, para que a decisão seja asserível
// sem construir o bus nem abrir conexão com o broker.
public static class RabbitMqSsl
{
    public const string Chave = "RabbitMq:UseSsl";

    // Default desligado: sem a chave, a conexão continua em texto claro, que é o que o broker
    // local de desenvolvimento espera. Valor presente e não-booleano derruba o host em vez de
    // cair no default — silenciar um "yes" faria o serviço conectar sem TLS achando que tem.
    public static bool Habilitado(IConfiguration configuration)
    {
        string? valor = configuration[Chave];

        if (valor is null)
        {
            return false;
        }

        return bool.TryParse(valor, out bool habilitado)
            ? habilitado
            : throw new InvalidOperationException(
                $"{Chave} deve ser 'true' ou 'false'; valor configurado: '{valor}'."
            );
    }
}
