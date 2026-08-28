using Microsoft.EntityFrameworkCore;

namespace BookRent.Infrastructure.Persistence;

/// <summary>
/// Contexto EF Core sobre o PostgreSQL — fonte de verdade do sistema.
/// As entidades sao mapeadas por <c>IEntityTypeConfiguration</c> em
/// <c>Persistence/Configurations</c> e carregadas automaticamente abaixo.
/// </summary>
public class BookRentDbContext(DbContextOptions<BookRentDbContext> options) : DbContext(options)
{
    /// <summary>Schema dedicado, para nao poluir o <c>public</c>.</summary>
    public const string Schema = "bookrent";

    // DbSet<Book>, DbSet<BookCopy>, DbSet<User>, DbSet<Loan>, DbSet<AuditEvent>
    // e DbSet<IdempotencyRecord> serao adicionados junto com o modelo de dominio.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BookRentDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Todo timestamp trafega e e persistido em UTC (timestamptz).
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<string>().HaveMaxLength(512);

        base.ConfigureConventions(configurationBuilder);
    }
}
