using BookRent.Application.Abstractions.Persistence;
using BookRent.Application.Loans;
using BookRent.Domain.Books;
using BookRent.Domain.Common;

namespace BookRent.Application.Books;

/// <summary>Disponibilidade atual do livro, como o desafio pede em um endpoint proprio.</summary>
public sealed record BookAvailabilityResponse(
    Guid BookId,
    string Title,
    int TotalCopies,
    int AvailableCopies,
    int ActiveLoans,
    bool IsActive)
{
    /// <summary>Ha exemplar para emprestar agora — informativo; quem decide e o banco.</summary>
    public bool Available => IsActive && AvailableCopies > 0;
}

/// <summary>
/// Projeta a disponibilidade a partir do MESMO snapshot cacheado que serve
/// <c>GET /books/{id}</c>. Uma chave, dois endpoints: eles leem literalmente os mesmos
/// bytes e nao conseguem divergir. Ver secao 5 do plano.
///
/// Isto e leitura informativa. A decisao de emprestar nunca passa por aqui — ela e o
/// UPDATE condicional no PostgreSQL, e e por isso que uma defasagem de cache pode
/// mostrar um numero velho numa tela, mas nunca causar um emprestimo errado.
/// </summary>
public sealed class GetBookAvailabilityHandler(GetBookHandler getBook)
{
    public async Task<BookAvailabilityResponse> GetAsync(Guid bookId, CancellationToken cancellationToken = default)
    {
        var book = await getBook.GetAsync(bookId, cancellationToken).ConfigureAwait(false);

        return new BookAvailabilityResponse(
            book.Id,
            book.Title,
            book.TotalCopies,
            book.AvailableCopies,
            book.ActiveLoans,
            book.IsActive);
    }
}

/// <summary>
/// Historico de emprestimos do livro, em qualquer estado. 404 aqui significa livro
/// inexistente, e nao ausencia de emprestimos.
/// </summary>
public sealed class GetBookHistoryHandler(IBookRepository books, ILoanRepository loans)
{
    public async Task<PagedResult<LoanResponse>> HandleAsync(
        Guid bookId,
        SearchLoansQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Inclui livro desativado de proposito: desativar nao pode esconder o historico.
        var book = await books.FindReadOnlyAsync(bookId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(BookErrors.NotFound, $"Livro {bookId} nao encontrado.");

        return await loans
            .SearchAsync((query with { BookId = book.Id }).Normalized(), cancellationToken)
            .ConfigureAwait(false);
    }
}
