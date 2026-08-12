using Fcg.Payments.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Messaging;

// Standalone (sem containers): a decisão de TLS é lida da configuração, então ela é asserível
// sem construir o bus nem abrir conexão. Fica fora da IntegrationCollection de propósito.
public class RabbitMqSslTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void DeveRefletirAChaveDeConfiguracao(string? valor, bool esperado)
    {
        IConfiguration configuration = Construir(valor);

        RabbitMqSsl.Habilitado(configuration).Should().Be(esperado);
    }

    [Fact]
    public void DeveRecusarValorNaoBooleano()
    {
        IConfiguration configuration = Construir("yes");

        Action ler = () => RabbitMqSsl.Habilitado(configuration);

        ler.Should().Throw<InvalidOperationException>().WithMessage("*UseSsl*");
    }

    // Valor nulo é a chave ausente: nada é semeado, para que o caso exercite mesmo a ausência.
    private static IConfiguration Construir(string? valor)
    {
        Dictionary<string, string?> entradas = [];

        if (valor is not null)
        {
            entradas[RabbitMqSsl.Chave] = valor;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(entradas).Build();
    }
}
