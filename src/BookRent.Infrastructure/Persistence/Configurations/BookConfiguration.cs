using BookRent.Domain.Books;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookRent.Infrastructure.Persistence.Configurations;

internal sealed class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("books", BookRentDbContext.Schema, table =>
        {
            // A invariante tambem vive no dominio (Book.RegisterCheckout) e no UPDATE
            // condicional do emprestimo. Aqui ela vira garantia: nem um bug futuro na
            // aplicacao consegue gravar quantidade negativa.
            table.HasCheckConstraint(
                "ck_books_available_within_total",
                "available_copies >= 0 AND available_copies <= total_copies");

            table.HasCheckConstraint("ck_books_total_copies_non_negative", "total_copies >= 0");
        });

        builder.HasKey(book => book.Id);

        // O id nasce no dominio (UUIDv7); o banco nao gera nada.
        builder.Property(book => book.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(book => book.Title)
            .HasColumnName("title")
            .HasMaxLength(Book.MaxTitleLength)
            .IsRequired();

        builder.Property(book => book.Isbn)
            .HasColumnName("isbn")
            .HasMaxLength(Book.MaxIsbnLength)
            .IsRequired();

        builder.Property(book => book.Author)
            .HasColumnName("author")
            .HasMaxLength(Book.MaxAuthorLength)
            .IsRequired();

        builder.Property(book => book.TotalCopies).HasColumnName("total_copies");
        builder.Property(book => book.AvailableCopies).HasColumnName("available_copies");
        builder.Property(book => book.IsActive).HasColumnName("is_active");
        builder.Property(book => book.CreatedAt).HasColumnName("created_at");
        builder.Property(book => book.UpdatedAt).HasColumnName("updated_at");
        builder.Property(book => book.DeactivatedAt).HasColumnName("deactivated_at");

        // Token de concorrencia gerenciado pela aplicacao, e nao xmin: xmin muda a cada
        // UPDATE na linha, inclusive os que so mexem no contador, e faria todo emprestimo
        // derrubar a edicao de catalogo em andamento. Ver secao 9.7 do plano.
        builder.Property(book => book.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasIndex(book => book.Isbn)
            .IsUnique()
            .HasDatabaseName("ux_books_isbn");

        // Busca textual de GET /books?q=: ILIKE '%termo%' nao aproveita B-tree porque o
        // padrao comeca com curinga. Trigramas (pg_trgm) indexam esse caso.
        builder.HasIndex(book => book.Title)
            .HasDatabaseName("ix_books_title_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        builder.HasIndex(book => book.Author)
            .HasDatabaseName("ix_books_author_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }
}
