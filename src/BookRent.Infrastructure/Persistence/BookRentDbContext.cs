using BookRent.Domain.Auditing;
using BookRent.Domain.Books;
using BookRent.Domain.Idempotency;
using BookRent.Domain.Loans;
using BookRent.Domain.Users;
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

    public DbSet<Book> Books => Set<Book>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Loan> Loans => Set<Loan>();

    /// <summary>Trilha append-only. Ver <see cref="GuardAuditTrail"/>.</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditTrail();

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAuditTrail();

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        // Necessaria para os indices GIN de trigrama que indexam a busca ILIKE
        // '%termo%' de GET /books?q= (ver BookConfiguration).
        modelBuilder.HasPostgresExtension("pg_trgm");

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

    /// <summary>
    /// A trilha de auditoria e append-only: alterar ou apagar um evento destruiria
    /// justamente a informacao que ela existe para preservar.
    ///
    /// Limite conhecido: isto protege o change tracker, nao o banco. Um
    /// <c>ExecuteUpdate</c>, um <c>ExecuteDelete</c> ou SQL cru passariam por cima —
    /// em producao a garantia definitiva seria revogar UPDATE/DELETE na tabela para o
    /// usuario da aplicacao.
    /// </summary>
    private void GuardAuditTrail()
    {
        var violation = ChangeTracker
            .Entries<AuditEvent>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (violation is not null)
        {
            throw new InvalidOperationException(
                $"A trilha de auditoria e append-only: {violation.State} nao e permitido em AuditEvent.");
        }
    }
}
