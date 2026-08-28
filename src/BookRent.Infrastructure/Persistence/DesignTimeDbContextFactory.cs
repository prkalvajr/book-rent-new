using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BookRent.Infrastructure.Persistence;

/// <summary>
/// Permite rodar <c>dotnet ef migrations</c> apontando apenas para este projeto,
/// sem precisar carregar a API. A connection string vem de
/// <c>ConnectionStrings__Postgres</c> ou usa o padrao local do Docker Compose.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BookRentDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=bookrent;Username=bookrent;Password=bookrent";

    public BookRentDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<BookRentDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", BookRentDbContext.Schema))
            .Options;

        return new BookRentDbContext(options);
    }
}
