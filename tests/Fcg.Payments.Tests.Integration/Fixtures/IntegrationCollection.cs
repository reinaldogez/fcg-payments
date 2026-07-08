using Xunit;

namespace Fcg.Payments.Tests.Integration.Fixtures;

// Um único Postgres (e RabbitMQ) compartilhado por toda a coleção; testes rodam em série.
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<PaymentsApiFactory>
{
    public const string Name = "Integration";
}
