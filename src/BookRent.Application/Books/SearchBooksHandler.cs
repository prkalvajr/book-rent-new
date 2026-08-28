using BookRent.Application.Abstractions.Persistence;

namespace BookRent.Application.Books;

/// <summary>
/// Listagem paginada do catalogo, com busca por titulo ou autor.
///
/// Deliberadamente SEM cache: enquanto a resposta trouxer disponibilidade, todo
/// emprestimo invalidaria todas as paginas e a taxa de acerto ficaria proxima de zero.
/// O custo real da busca se resolve com indice GIN de trigrama, nao com cache —
/// indice deixa rapido sem criar obrigacao de coerencia. Ver secao 5.1 do plano.
/// </summary>
public sealed class SearchBooksHandler(IBookRepository books)
{
    public Task<PagedResult<BookResponse>> HandleAsync(
        SearchBooksQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return books.SearchAsync(query.Normalized(), cancellationToken);
    }
}
