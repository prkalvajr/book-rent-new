using BookRent.Application.Abstractions.Auditing;
using BookRent.Application.Abstractions.Caching;
using BookRent.Application.Abstractions.Persistence;
using BookRent.Infrastructure.Auditing;
using BookRent.Infrastructure.Caching;
using BookRent.Infrastructure.Health;
using BookRent.Infrastructure.Persistence;
using BookRent.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BookRent.Infrastructure;

/// <summary>Registro dos adaptadores de infraestrutura (PostgreSQL, Redis, health checks).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddPersistence()
            .AddCaching(configuration)
            .AddInfrastructureHealthChecks();

        return services;
    }

    private static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        // A connection string e lida na construcao do servico, e nao no registro:
        // assim overrides aplicados depois (testes de integracao, variaveis de
        // ambiente injetadas pelo orquestrador) sao respeitados.
        services.AddDbContext<BookRentDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                ResolveConnectionString(serviceProvider.GetRequiredService<IConfiguration>(), "Postgres"),
                npgsql =>
                {
                    npgsql.MigrationsAssembly(typeof(BookRentDbContext).Assembly.FullName);
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", BookRentDbContext.Schema);

                    // Retry so cobre falhas transitorias de rede; conflitos de concorrencia
                    // sao tratados explicitamente pelo caso de uso.
                    npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null);
                }));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IBookRepository, BookRepository>();
        services.AddScoped<IAuditTrail, AuditTrail>();

        return services;
    }

    private static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CacheOptions>()
            .Bind(configuration.GetSection(CacheOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
            ConnectionMultiplexer.Connect(
                BuildRedisOptions(serviceProvider.GetRequiredService<IConfiguration>())));

        services.AddStackExchangeRedisCache(_ => { });
        services.AddOptions<RedisCacheOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                options.ConfigurationOptions = BuildRedisOptions(config);
                options.InstanceName = config.GetSection(CacheOptions.SectionName)["InstanceName"] ?? "bookrent:";
            });

        services.AddScoped<ICacheStore, RedisCacheStore>();

        return services;
    }

    private static IServiceCollection AddInfrastructureHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("Processo ativo"), tags: [HealthCheckTags.Live])
            .AddDbContextCheck<BookRentDbContext>("postgres", tags: [HealthCheckTags.Ready])
            .AddCheck<RedisHealthCheck>("redis", tags: [HealthCheckTags.Ready]);

        return services;
    }

    private static ConfigurationOptions BuildRedisOptions(IConfiguration configuration)
    {
        var options = ConfigurationOptions.Parse(ResolveConnectionString(configuration, "Redis"));

        // O processo sobe mesmo com o Redis fora do ar; quem responde por isso
        // e o /health/ready, nao o /health/live.
        options.AbortOnConnectFail = false;

        return options;
    }

    private static string ResolveConnectionString(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException($"ConnectionStrings:{name} nao configurada.");
}
