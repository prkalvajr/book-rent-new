using BookRent.Application.Books;
using BookRent.Application.Loans;
using BookRent.Domain.Loans;

namespace BookRent.Application.Abstractions.Persistence;

public interface ILoanRepository
{
    void Add(Loan loan);

    Task<Loan?> FindReadOnlyAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Historico paginado, por usuario ou por livro. Devolve emprestimos em qualquer
    /// estado: devolvidos e cancelados continuam aparecendo, que e justamente o ponto.
    /// </summary>
    Task<PagedResult<LoanResponse>> SearchAsync(
        SearchLoansQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Muda o status com UM comando guardado pelo status atual:
    /// <c>UPDATE ... SET status = @novo WHERE id = @id AND status = 'Active'</c>.
    ///
    /// Devolve 1 na transicao e 0 quando o emprestimo ja nao estava ativo. Isso torna
    /// devolucao e cancelamento idempotentes por construcao — uma segunda chamada nao
    /// tem efeito — e dispensa <c>Idempotency-Key</c> nesses endpoints. Ver secao 2.3.
    /// </summary>
    Task<int> TryTransitionFromActiveAsync(
        Guid loanId,
        LoanStatus newStatus,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
