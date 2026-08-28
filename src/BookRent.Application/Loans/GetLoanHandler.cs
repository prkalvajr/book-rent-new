using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Common;
using BookRent.Domain.Loans;

namespace BookRent.Application.Loans;

/// <summary>
/// Consulta de um emprestimo. Existe porque POST /loans devolve Location apontando
/// para ca — um Location que responde 404 seria contrato quebrado.
///
/// Devolve o emprestimo em qualquer estado: devolvido e cancelado continuam
/// consultaveis, que e o proposito de nunca apagar o registro.
/// </summary>
public sealed class GetLoanHandler(ILoanRepository loans)
{
    public async Task<LoanResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var loan = await loans.FindReadOnlyAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(LoanErrors.NotFound, $"Emprestimo {id} nao encontrado.");

        return loan.ToResponse();
    }
}
