using Fcg.Payments.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Fcg.Payments.Tests.Integration.Fixtures;

// Sobe a API real contra Postgres + RabbitMQ do Testcontainers. O RabbitMQ é compartilhado por
// toda a coleção e sobe desde já — fica ocioso até o wiring de mensageria existir; o custo de
// mantê-lo de pé é desprezível perto do retrabalho de recriar a fixture depois. Os hosted
// services do MassTransit são removidos no host de teste — o sweeper do Outbox dá deadlock com
// o reset de banco entre testes, e IBus/IPublishEndpoint seguem resolvíveis sem ele.
public class PaymentsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("payments")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder(
        "rabbitmq:3.13-management-alpine"
    ).Build();

    // Connection amqp do container, para o teste de topologia abrir um canal direto e inspecionar
    // fila/exchange declaradas pelo bus (o bus é iniciado sob demanda, não pela fixture).
    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Satisfaz o fail-fast de connection string do startup e aponta o DbContext ao container.
        // O container já está de pé (StartAsync precede o build do host em InitializeAsync).
        builder.UseSetting("ConnectionStrings:Payments", _postgres.GetConnectionString());

        // Host do bus por campos separados, lidos tarde do container (porta dinâmica). Deriva
        // os quatro campos da connection string amqp do Testcontainer.
        Uri amqp = new(_rabbitMq.GetConnectionString());
        string[] credenciais = amqp.UserInfo.Split(':', 2);
        builder.UseSetting("RabbitMq:Host", amqp.Host);
        builder.UseSetting("RabbitMq:Port", amqp.Port.ToString());
        builder.UseSetting("RabbitMq:Username", Uri.UnescapeDataString(credenciais[0]));
        builder.UseSetting("RabbitMq:Password", Uri.UnescapeDataString(credenciais[1]));

        builder.ConfigureTestServices(services =>
        {
            // Sem os hosted services do MassTransit, o bus não sobe sozinho (evita o deadlock do
            // sweeper do Outbox com o reset de banco). O bus é iniciado sob demanda nos testes de
            // topologia; o IPublishEndpoint real do MassTransit segue resolvível e o publish cai
            // na OutboxMessage via DbContext.
            services.RemoveAll<IHostedService>();
        });
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        using IServiceScope scope = Services.CreateScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await db.Database.MigrateAsync();
    }

    // Reset entre testes (coleção em série): as tabelas voltam vazias.
    public async Task ResetAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        // Cobre também o estado do Inbox/Outbox: sem isso a dedup por MessageId de uma execução
        // vazaria para a seguinte (as tabelas do MassTransit vivem no mesmo contexto).
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE pagamentos, inbox_state, outbox_message, outbox_state RESTART IDENTITY CASCADE;"
        );
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await base.DisposeAsync();
    }
}
