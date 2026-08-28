using BookRent.Application.Books;
using BookRent.Domain.Books;

namespace BookRent.Application.Abstractions.Persistence;

/// <summary>
/// Porta de acesso ao catalogo. A implementacao vive na infraestrutura, sobre o EF Core.
/// </summary>
public interface IBookRepository
{
    void Add(Book book);

    /// <summary>Carrega rastreado pelo change tracker, para alteracao via SaveChanges.</summary>
    Task<Book?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Carrega sem rastreamento, para leitura ou para validar antes de um UPDATE explicito.</summary>
    Task<Book?> FindReadOnlyAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> IsbnExistsAsync(string isbn, Guid? excludingBookId, CancellationToken cancellationToken = default);

    Task<bool> HasActiveLoansAsync(Guid bookId, CancellationToken cancellationToken = default);

    Task<PagedResult<BookResponse>> SearchAsync(SearchBooksQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aplica a alteracao de catalogo como UM comando condicional, e devolve o numero de
    /// linhas afetadas (0 ou 1).
    ///
    /// Nao passa pelo change tracker de proposito. Os campos descritivos sao escrita
    /// absoluta e vao protegidos por <paramref name="expectedVersion"/> no WHERE; ja
    /// <c>available_copies</c> e escrita RELATIVA
    /// (<c>available_copies + availabilityDelta</c>), porque um emprestimo concorrente
    /// pode te-la mudado sem tocar em <c>version</c> — gravar o valor lido de volta
    /// perderia esse emprestimo. Ver secoes 2.4 e 9.7 do plano.
    ///
    /// O WHERE tambem carrega <c>available_copies + delta &gt;= 0</c>: a condicao e
    /// avaliada pelo banco contra o valor corrente, entao ela sozinha impede reduzir o
    /// acervo abaixo dos emprestimos ativos.
    /// </summary>
    Task<int> ApplyCatalogUpdateAsync(
        Book book,
        int expectedVersion,
        int availabilityDelta,
        CancellationToken cancellationToken = default);
}
