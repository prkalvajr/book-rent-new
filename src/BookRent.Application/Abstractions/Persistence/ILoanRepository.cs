using BookRent.Application.Books;
using BookRent.Application.Loans;

namespace BookRent.Application.Abstractions.Persistence;

public interface ILoanRepository
{
    /// <summary>
    /// Historico paginado, por usuario ou por livro. Devolve empréstimos em qualquer
    /// estado: devolvidos e cancelados continuam aparecendo, que e justamente o ponto.
    /// </summary>
    Task<PagedResult<LoanResponse>> SearchAsync(
        SearchLoansQuery query,
        CancellationToken cancellationToken = default);
}
