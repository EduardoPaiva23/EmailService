using EmailService.Application;
using EmailService.Application.Security;
using EmailService.Domain.Entities;
using EmailService.Infrastructure;
using EmailService.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace EmailService.IntegrationTests.Persistence;

/// <summary>
/// Sobe um SQL Server descartável (Testcontainers), aplica as migrations e
/// expõe escopos de DI com a composição real de Application + Infrastructure.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string DevApiKey = "es_dev_integration_test_key_0123456789";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        // Mesmo limite do docker-compose: evita esgotar a memória do host.
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
        .Build();

    private ServiceProvider _provider = null!;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EmailServiceTests"
        }.ConnectionString;

        _provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EmailServiceDb"] = ConnectionString,
            ["DevelopmentSeed:ApiKey"] = DevApiKey,
            ["DevelopmentSeed:ClientName"] = "Cliente de teste"
        });

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public static ServiceProvider BuildProvider(IDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection()
            .AddLogging()
            .AddApplication()
            .AddInfrastructure(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Cria um ApiClient com uma API key conhecida, para chamar a API.</summary>
    public async Task<(ApiClient Client, string ApiKey)> CreateClientWithKeyAsync()
    {
        var apiKey = "es_test_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var client = ApiClient.Create(
            "Cliente " + apiKey[8..14],
            ApiKeyHasher.GetPrefix(apiKey),
            ApiKeyHasher.Hash(apiKey),
            DateTime.UtcNow);

        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        return (client, apiKey);
    }

    /// <summary>Cria e persiste um ApiClient novo, para isolar os dados de cada teste.</summary>
    public async Task<ApiClient> CreateClientAsync()
    {
        var client = ApiClient.Create(
            "Cliente " + Guid.NewGuid().ToString("N")[..6],
            keyPrefix: Guid.NewGuid().ToString("N")[..12],
            keyHash: Guid.NewGuid().ToString("N"),
            createdAt: DateTime.UtcNow);

        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
