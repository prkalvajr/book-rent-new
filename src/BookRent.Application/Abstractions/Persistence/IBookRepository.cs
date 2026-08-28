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

    /// <summary>
    /// Reserva um exemplar com UM comando condicional atomico:
    /// <c>UPDATE ... SET available_copies = available_copies - 1
    /// WHERE id = @id AND is_active AND available_copies &gt; 0</c>.
    ///
    /// Devolve 1 quando reservou e 0 quando nao havia exemplar. Nao existe janela entre
    /// ler e decidir: o comando avalia o predicado e escreve no mesmo passo. Sob
    /// READ COMMITTED, a segunda transacao espera o row lock e o PostgreSQL reavalia o
    /// WHERE contra a versao nova da linha — com um exemplar so, ela encontra 0 e afeta
    /// zero linhas. Ver secao 2.1 do plano.
    ///
    /// NAO incrementa <c>version</c>: emprestimo nao pode invalidar uma edicao de
    /// catalogo em andamento (secao 9.7).
    /// </summary>
    Task<int> TryReserveCopyAsync(Guid bookId, CancellationToken cancellationToken = default);

    /// <summary>Devolve um exemplar a circulacao, sem ultrapassar o acervo.</summary>
    Task<int> ReleaseCopyAsync(Guid bookId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Desativa o livro com a checagem de emprestimo ativo DENTRO do proprio UPDATE:
    /// <c>available_copies = total_copies</c> equivale a "zero emprestimos ativos" pela
    /// invariante, e o banco avalia isso contra o valor corrente.
    ///
    /// Um SELECT previo nao serviria: emprestimo nao altera <c>Version</c> (secao 9.7),
    /// entao um emprestimo criado entre a leitura e a gravacao passaria despercebido e o
    /// livro terminaria inativo com exemplar emprestado.
    /// </summary>
    Task<int> DeactivateIfNoActiveLoansAsync(
        Book book,
        int expectedVersion,
        CancellationToken cancellationToken = default);
}
