using BookRent.Domain.Books;
using BookRent.Domain.Loans;
using BookRent.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookRent.Infrastructure.Persistence.Configurations;

internal sealed class LoanConfiguration : IEntityTypeConfiguration<Loan>
{
    public void Configure(EntityTypeBuilder<Loan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("loans", BookRentDbContext.Schema, table =>
            table.HasCheckConstraint(
                "ck_loans_status",
                "status IN ('Active', 'Returned', 'Cancelled')"));

        builder.HasKey(loan => loan.Id);

        builder.Property(loan => loan.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(loan => loan.BookId).HasColumnName("book_id");
        builder.Property(loan => loan.UserId).HasColumnName("user_id");

        // Texto, e nao int: um dump continua legivel e renumerar o enum nao corrompe
        // dados em silencio. A CHECK acima impede valor fora do conjunto.
        builder.Property(loan => loan.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(loan => loan.LoanedAt).HasColumnName("loaned_at");
        builder.Property(loan => loan.DueAt).HasColumnName("due_at");
        builder.Property(loan => loan.ReturnedAt).HasColumnName("returned_at");
        builder.Property(loan => loan.CancelledAt).HasColumnName("cancelled_at");

        builder.Property(loan => loan.Actor)
            .HasColumnName("actor")
            .HasMaxLength(Loan.MaxActorLength)
            .IsRequired();

        // RESTRICT nos dois lados: o historico nao pode desaparecer junto com o livro
        // ou o usuario. Remocao de livro e desativacao, nunca DELETE.
        builder.HasOne<Book>()
            .WithMany()
            .HasForeignKey(loan => loan.BookId)
            .HasConstraintName("fk_loans_books")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(loan => loan.UserId)
            .HasConstraintName("fk_loans_users")
            .OnDelete(DeleteBehavior.Restrict);

        // Historico por livro e por usuario, do mais recente para o mais antigo.
        builder.HasIndex(loan => new { loan.BookId, loan.LoanedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_loans_book_loaned_at");

        builder.HasIndex(loan => new { loan.UserId, loan.LoanedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_loans_user_loaned_at");

        // Indice parcial, so com as linhas ativas: serve a checagem de emprestimo ativo
        // feita pelo DELETE de livro, que e a pergunta mais frequente sobre esta tabela.
        builder.HasIndex(loan => loan.BookId)
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ix_loans_active_by_book");
    }
}
