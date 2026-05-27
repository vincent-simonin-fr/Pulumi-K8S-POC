using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Order.Infrastructure.Persistence;
using Respawn;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Order.Application.IntegrationTests;

/// <summary>
/// Fixture partagée par tous les tests — démarre PostgreSQL et RabbitMQ
/// dans des containers, réinitialise la base entre chaque test.
/// </summary>
[CollectionDefinition(nameof(IntegrationTestCollection))]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>;

public class IntegrationTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:tag")
        .WithImage("postgres:16-alpine")
        .WithDatabase("order_db_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RabbitMqContainer _rabbitmq = new RabbitMqBuilder("rabbitmq:tag")
        .WithImage("rabbitmq:3.13-alpine")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = default!;
    public HttpClient Client { get; private set; } = default!;
    private Respawner _respawner = default!;
    private string _connectionString = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitmq.StartAsync());

        _connectionString = _postgres.GetConnectionString();

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Remplacer la vraie connection string par celle du container
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                    if (descriptor is not null) services.Remove(descriptor);

                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseNpgsql(_connectionString));

                    // Remplacer la config RabbitMQ
                    services.Configure<Microsoft.Extensions.Configuration.IConfiguration>(_ => { });
                });

                builder.UseSetting("ConnectionStrings:OrderDb",  _connectionString);
                builder.UseSetting("RabbitMQ:Host",              _rabbitmq.Hostname);
                builder.UseSetting("RabbitMQ:Username",          "guest");
                builder.UseSetting("RabbitMQ:Password",          "guest");
            });

        Client = Factory.CreateClient();

        // Appliquer les migrations
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();

        _respawner = await Respawner.CreateAsync(
            _connectionString,
            new RespawnerOptions { DbAdapter = DbAdapter.Postgres });
    }

    public async Task ResetDatabaseAsync() => await _respawner.ResetAsync(_connectionString);

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbitmq.DisposeAsync();
    }
}
