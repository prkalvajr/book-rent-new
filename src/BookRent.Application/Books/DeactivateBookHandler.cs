using BookRent.Application.Abstractions.Auditing;
using BookRent.Application.Abstractions.Caching;
using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Auditing;
using BookRent.Domain.Books;
using BookRent.Domain.Common;

namespace BookRent.Application.Books;

/// <summary>
/// <c>DELETE /books/{id}</c> desativa; nunca apaga. Com emprestimos ATIVOS, recusa —
/// o desafio pede "desativar ou rejeitar a remocao", e historico encerrado nao impede
/// a desativacao, so o registro nunca some.
///
/// Diferente do <see cref="UpdateBookHandler"/>, este caminho usa o change tracker:
/// ele escreve apenas campos descritivos (<c>is_active</c>, <c>deactivated_at</c>,
/// <c>version</c>) e nao encosta no contador, entao a escrita absoluta e segura e o
/// token de concorrencia do EF Core basta.
/// </summary>
public sealed class DeactivateBookHandler(
    IBookRepository books,
    IUnitOfWork unitOfWork,
    IAuditTrail auditTrail,
    ICacheStore cache,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var book = await books.FindAsync(id, ct).ConfigureAwait(false)
                    ?? throw new DomainException(BookErrors.NotFound, $"Livro {id} nao encontrado.");

                if (await books.HasActiveLoansAsync(id, ct).ConfigureAwait(false))
                {
                    throw new DomainException(
                        BookErrors.HasActiveLoans,
                        "O livro possui emprestimos ativos e nao pode ser desativado.");
                }

                book.Deactivate(now);

                auditTrail.Record(
                    AuditEntityTypes.Book,
                    id,
                    AuditActions.BookDeactivated,
                    new
                    {
                        book.Title,
                        book.Isbn,
                        DeactivatedAt = now,
                    });

                return book;
            },
            cancellationToken).ConfigureAwait(false);

        await cache.RemoveAsync(CacheKeys.Book(id), cancellationToken).ConfigureAwait(false);
    }
}
