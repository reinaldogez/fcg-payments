using Fcg.Payments.Tests.Integration.Fixtures;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Persistence;

[Collection(IntegrationCollection.Name)]
public abstract class IntegrationTestBase(PaymentsApiFactory factory) : IAsyncLifetime
{
    protected PaymentsApiFactory Factory { get; } = factory;

    public Task InitializeAsync() => Factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;
}
