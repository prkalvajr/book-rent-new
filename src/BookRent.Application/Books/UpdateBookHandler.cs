using BookRent.Application.Abstractions.Auditing;
using BookRent.Application.Abstractions.Caching;
using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Auditing;
using BookRent.Domain.Books;
using BookRent.Domain.Common;

namespace BookRent.Application.Books;

/// <summary>
/// Alteracao de catalogo com concorrencia otimista.
///
/// O livro e carregado SEM rastreamento e as mudancas passam pelos metodos do dominio,
/// que validam e calculam. A gravacao, porem, e um UPDATE condicional explicito, e nao
/// um SaveChanges: o change tracker so sabe escrever valores absolutos, e
/// <c>available_copies</c> precisa de escrita relativa para nao perder um emprestimo
/// concorrente que mudou a disponibilidade sem tocar em <c>version</c>. Ver secao 9.7.
/// </summary>
public sealed class UpdateBookHandler(
    IBookRepository books,
    IUnitOfWork unitOfWork,
    IAuditTrail auditTrail,
    ICacheStore cache,
    TimeProvider timeProvider)
{
    public async Task<BookResponse> HandleAsync(
        Guid id,
        UpdateBookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = timeProvider.GetUtcNow();

        var updated = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var book = await books.FindReadOnlyAsync(id, ct).ConfigureAwait(false)
                    ?? throw new DomainException(BookErrors.NotFound, $"Livro {id} nao encontrado.");

                var before = book.ToResponse();
                var originalVersion = book.Version;
                var originalTotal = book.TotalCopies;

                // PATCH: campo ausente permanece como esta.
                book.UpdateDetails(
                    request.Title ?? book.Title,
                    request.Isbn ?? book.Isbn,
                    request.Author ?? book.Author,
                    now);

                if (request.TotalCopies is { } newTotal)
                {
                    book.AdjustTotalCopies(newTotal, now);
                }

                if (await books.IsbnExistsAsync(book.Isbn, excludingBookId: id, ct).ConfigureAwait(false))
                {
                    throw new DomainException(
                        BookErrors.IsbnAlreadyExists,
                        $"Ja existe outro livro com o ISBN {book.Isbn}.");
                }

                // Quando o cliente informa a versao que leu, e ela que guarda o UPDATE.
                // Sem ela, guardamos pela versao carregada agora — protege contra uma
                // escrita concorrente ocorrida durante esta propria transacao.
                var expectedVersion = request.ExpectedVersion ?? originalVersion;
                var delta = book.TotalCopies - originalTotal;

                var affected = await books
                    .ApplyCatalogUpdateAsync(book, expectedVersion, delta, ct)
                    .ConfigureAwait(false);

                if (affected == 0)
                {
                    await ThrowForFailedUpdateAsync(id, expectedVersion, delta, ct).ConfigureAwait(false);
                }

                auditTrail.Record(
                    AuditEntityTypes.Book,
                    id,
                    AuditActions.BookUpdated,
                    new
                    {
                        Before = new { before.Title, before.Isbn, before.Author, before.TotalCopies },
                        After = new { book.Title, book.Isbn, book.Author, book.TotalCopies },
                    });

                // A resposta vem de uma RELEITURA, e nao da entidade em memoria. Ela
                // divergiria do banco em dois pontos:
                //
                //   Version — os metodos do dominio incrementam a cada chamada, entao um
                //   PATCH que altera descritivos E quantidade deixaria a entidade em
                //   +2 enquanto o UPDATE grava +1. O cliente usaria essa versao no proximo
                //   PATCH e levaria um 409 espurio.
                //
                //   AvailableCopies — o UPDATE soma o delta ao valor CORRENTE (escrita
                //   relativa, secao 9.7). Um emprestimo concorrente torna o valor lido
                //   obsoleto, e a entidade nao sabe disso.
                //
                // Uma consulta a mais numa operacao administrativa e rara; devolver numero
                // errado sai mais caro.
                return await books.FindReadOnlyAsync(id, ct).ConfigureAwait(false)
                    ?? throw new DomainException(BookErrors.NotFound, $"Livro {id} nao encontrado.");
            },
            cancellationToken).ConfigureAwait(false);

        await cache.RemoveAsync(CacheKeys.Book(id), cancellationToken).ConfigureAwait(false);

        return updated.ToResponse();
    }

    /// <summary>
    /// Zero linhas afetadas nao diz QUAL das duas condicoes do WHERE falhou. A releitura
    /// separa "alguem alterou o livro enquanto voce editava" (409) de "isso reduziria o
    /// acervo abaixo dos emprestimos ativos" (422). Uma consulta a mais, so no erro.
    /// </summary>
    private async Task ThrowForFailedUpdateAsync(
        Guid id,
        int expectedVersion,
        int delta,
        CancellationToken cancellationToken)
    {
        var current = await books.FindReadOnlyAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(BookErrors.NotFound, $"Livro {id} nao encontrado.");

        if (current.Version != expectedVersion)
        {
            throw new DomainException(
                BookErrors.ConcurrentModification,
                $"O livro foi alterado por outra operacao (versao atual: {current.Version}, esperada: {expectedVersion}).");
        }

        if (current.AvailableCopies + delta < 0)
        {
            throw new DomainException(
                BookErrors.TotalBelowActiveLoans,
                $"Nao e possivel reduzir o acervo: {current.ActiveLoans} exemplares estao emprestados.");
        }

        // Nem versao nem invariante: o livro foi desativado no intervalo.
        throw new DomainException(BookErrors.Inactive, "O livro esta desativado.");
    }
}
