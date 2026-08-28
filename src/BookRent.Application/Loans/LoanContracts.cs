using BookRent.Domain.Loans;

namespace BookRent.Application.Loans;

/// <summary>
/// Emprestimo como o cliente o ve. O registro nunca e apagado: devolucao e cancelamento
/// aparecem como <c>Status</c> mais o instante correspondente, e e isso que preserva o
/// historico exigido pelo desafio.
/// </summary>
public sealed record LoanResponse(
    Guid Id,
    Guid BookId,
    Guid UserId,
    string Status,
    DateTimeOffset LoanedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ReturnedAt,
    DateTimeOffset? CancelledAt,
    string Actor);

/// <summary>Filtro do historico, por usuario ou por livro.</summary>
public sealed record SearchLoansQuery(
    Guid? UserId,
    Guid? BookId,
    LoanStatus? Status,
    int Page,
    int PageSize)
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;

    public SearchLoansQuery Normalized() => this with
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
