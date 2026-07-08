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
        corpo
            .RootElement.GetProperty("checks")
            .EnumerateArray()
            .Should()
            .Contain(check => check.GetProperty("name").GetString() == "postgres");
    }

    [Fact]
    public async Task AgregadoDeveRetornar200()
    {
        HttpClient client = Factory.CreateClient();

        HttpResponseMessage resposta = await client.GetAsync("/health");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
