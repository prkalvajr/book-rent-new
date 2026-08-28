namespace BookRent.Application.Books;

/// <summary>Pagina de resultados, com o suficiente para o cliente navegar.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNext => Page * PageSize < TotalCount;
}

/// <summary>
/// Representacao do livro devolvida pela API e guardada no cache.
///
/// E tambem a forma do snapshot em <c>bookrent:book:{id}</c>: uma chave so, servindo
/// <c>GET /books/{id}</c> e <c>GET /books/{id}/availability</c>, que projeta os dois
/// numeros deste mesmo objeto. Ver secao 5 do plano.
/// </summary>
public sealed record BookResponse(
    Guid Id,
    string Title,
    string Isbn,
    string Author,
    int TotalCopies,
    int AvailableCopies,
    int ActiveLoans,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeactivatedAt);

public sealed record CreateBookRequest(string? Title, string? Isbn, string? Author, int TotalCopies);

/// <summary>
/// Campos ausentes (null) permanecem inalterados — semantica de PATCH.
/// <see cref="ExpectedVersion"/> e o token de concorrencia otimista: quando informado,
/// a alteracao so acontece se o livro nao tiver mudado desde a leitura do cliente.
/// </summary>
public sealed record UpdateBookRequest(
    string? Title,
    string? Isbn,
    string? Author,
    int? TotalCopies,
    int? ExpectedVersion);

public sealed record SearchBooksQuery(string? Query, bool IncludeInactive, int Page, int PageSize)
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;

    /// <summary>Normaliza a paginacao: pagina minima 1, tamanho entre 1 e 100.</summary>
    public SearchBooksQuery Normalized() => this with
    {
        Page = Page < 1 ? 1 : Page,
        PageSize = PageSize switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => PageSize,
        },
    };
}
