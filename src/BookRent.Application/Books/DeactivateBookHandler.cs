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
                var book = await books.FindReadOnlyAsync(id, ct).ConfigureAwait(false)
                    ?? throw new DomainException(BookErrors.NotFound, $"Livro {id} nao encontrado.");

                book.Deactivate(now);

                // A checagem de emprestimo ativo vai NO PROPRIO UPDATE, e nao num SELECT
                // antes dele. Um SELECT sem lock deixava esta corrida aberta:
                //
                //   t1  DELETE  le "sem emprestimo ativo"
                //   t2  POST /loans  decrementa e cria o emprestimo, e commita
                //   t3  DELETE  grava — e passa, porque emprestimo NAO altera Version
                //               (por desenho, secao 9.7)
                //
                // O livro terminava inativo com emprestimo ativo. A condicao
                // available_copies = total_copies equivale a "zero emprestimos ativos"
                // pela invariante, e o banco a avalia contra o valor corrente.
                var affected = await books
                    .DeactivateIfNoActiveLoansAsync(book, expectedVersion: book.Version - 1, ct)
                    .ConfigureAwait(false);

                if (affected == 0)
                {
                    await ThrowForFailedDeactivationAsync(id, ct).ConfigureAwait(false);
                }

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

        // CancellationToken.None de proposito: a transacao ja commitou. Se o cliente
        // desconectasse aqui, o token cancelado abortaria o DEL — o RedisCacheStore
        // repassa OperationCanceledException — e o cache serviria dado obsoleto pela
        // janela inteira do TTL, com o banco ja alterado.
        await cache.RemoveAsync(CacheKeys.Book(id), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Zero linhas nao diz qual condicao falhou. A releitura separa "alguem alterou o
    /// livro", "ja estava desativado" e "tem emprestimo ativo".
    /// </summary>
    private async Task ThrowForFailedDeactivationAsync(Guid id, CancellationToken cancellationToken)
    {
        var current = await books.FindReadOnlyAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(BookErrors.NotFound, $"Livro {id} nao encontrado.");

        if (!current.IsActive)
        {
            throw new DomainException(BookErrors.AlreadyInactive, "O livro ja esta desativado.");
        }

        throw new DomainException(
            BookErrors.HasActiveLoans,
            $"O livro possui {current.ActiveLoans} emprestimo(s) ativo(s) e nao pode ser desativado.");
    }
}
