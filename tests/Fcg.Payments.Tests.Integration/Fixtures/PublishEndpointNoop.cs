using MassTransit;

namespace Fcg.Payments.Tests.Integration.Fixtures;

// Test double de IPublishEndpoint: o publish real (Outbox/broker) é cabeado numa etapa
// posterior. Aqui o no-op resolve a dependência para o host da fixture compor e subir.
internal sealed class PublishEndpointNoop : IPublishEndpoint
{
    public Task Publish<T>(T message, CancellationToken cancellationToken = default)
        where T : class => Task.CompletedTask;

    public Task Publish<T>(
        T message,
        IPipe<PublishContext<T>> publishPipe,
        CancellationToken cancellationToken = default
    )
        where T : class => Task.CompletedTask;

    public Task Publish<T>(
        T message,
        IPipe<PublishContext> publishPipe,
        CancellationToken cancellationToken = default
    )
        where T : class => Task.CompletedTask;

    public Task Publish(object message, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task Publish(
        object message,
        IPipe<PublishContext> publishPipe,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task Publish(
        object message,
        Type messageType,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task Publish(
        object message,
        Type messageType,
        IPipe<PublishContext> publishPipe,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task Publish<T>(object values, CancellationToken cancellationToken = default)
        where T : class => Task.CompletedTask;

    public Task Publish<T>(
        object values,
        IPipe<PublishContext<T>> publishPipe,
        CancellationToken cancellationToken = default
    )
        where T : class => Task.CompletedTask;

    public Task Publish<T>(
        object values,
        IPipe<PublishContext> publishPipe,
        CancellationToken cancellationToken = default
    )
        where T : class => Task.CompletedTask;

    public ConnectHandle ConnectPublishObserver(IPublishObserver observer) =>
        throw new NotSupportedException();
}
