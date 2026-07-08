using System.Net;
using System.Text.Json;
using Fcg.Payments.Tests.Integration.Fixtures;
using Fcg.Payments.Tests.Integration.Persistence;
using FluentAssertions;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Endpoints;

public class HealthEndpointsTests(PaymentsApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task LiveDeveRetornar200()
    {
        HttpClient client = Factory.CreateClient();

        HttpResponseMessage resposta = await client.GetAsync("/health/live");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadyDeveRetornar200ComPostgresSaudavel()
    {
        HttpClient client = Factory.CreateClient();

        HttpResponseMessage resposta = await client.GetAsync("/health/ready");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);

        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        corpo.RootElement.GetProperty("status").GetString().Should().Be("Healthy");

        var checks = corpo
            .RootElement.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString()!)
            .ToList();

        // Readiness é só Postgres: o check do bus perdeu a tag "ready" (o Outbox desacopla a
        // entrega do broker, então broker fora não deve derrubar a prontidão).
        checks.Should().Contain("postgres");
        checks.Should().NotContain(nome => nome.StartsWith("masstransit"));
    }

    [Fact]
    public async Task AgregadoDeveIncluirBusERefletirQueEleNaoIniciou()
    {
        HttpClient client = Factory.CreateClient();

        HttpResponseMessage resposta = await client.GetAsync("/health");

        // O agregado informa TODOS os checks. Neste host de teste os hosted services do MassTransit
        // são removidos, então o bus não inicia e seu check reporta não-saudável — daí o 503. Isto
        // é a contraprova do ready: o check do bus existe e está registrado, mas fica fora do ready
        // (o /health/ready segue 200 só com o Postgres). Um bus não-iniciado não derruba a prontidão.
        resposta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var checks = corpo
            .RootElement.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString()!)
            .ToList();

        checks.Should().Contain("postgres");
        checks.Should().Contain(nome => nome.StartsWith("masstransit"));
    }
}
