using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace BookRent.IntegrationTests.Fixtures;

/// <summary>
/// Sobe PostgreSQL e Redis reais em containers descartaveis e hospeda a API
/// em memoria apontando para eles. Nada de banco in-memory: os testes de
/// concorrencia dependem do comportamento real do PostgreSQL.
/// </summary>
public sealed class BookRentApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("bookrent")
        .WithUsername("bookrent")
        .WithPassword("bookrent")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine")
        .Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync()).ConfigureAwait(false);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = PostgresConnectionString,
                ["ConnectionStrings:Redis"] = RedisConnectionString,
                ["Database:MigrateOnStartup"] = "true",
                ["Serilog:MinimumLevel:Default"] = "Warning",
            }));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        await _postgres.DisposeAsync().ConfigureAwait(false);
        await _redis.DisposeAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }
}
