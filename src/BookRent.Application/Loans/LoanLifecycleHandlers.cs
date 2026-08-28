using BookRent.Application.Abstractions.Auditing;
using BookRent.Application.Abstractions.Caching;
using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Auditing;
using BookRent.Domain.Common;
using BookRent.Domain.Loans;

namespace BookRent.Application.Loans;

/// <summary>
/// Devolucao e cancelamento. Os dois seguem a mesma forma, e por isso compartilham a
/// implementacao: transicao guardada pelo status atual, exemplar devolvido a circulacao,
/// auditoria e invalidacao de cache apos o commit.
///
/// Nao exigem <c>Idempotency-Key</c>: a guarda <c>WHERE status = 'Active'</c> ja torna a
/// operacao idempotente por construcao — a segunda chamada afeta zero linhas. Ver secao 2.3.
///
/// O registro NUNCA e apagado: so muda de status, e continua no historico do livro e do
/// leitor. E isso que o desafio pede ao exigir que cancelar preserve a informacao de que
/// o emprestimo existiu.
/// </summary>
public sealed class LoanLifecycleHandler(
    ILoanRepository loans,
    IBookRepository books,
    IUnitOfWork unitOfWork,
    IAuditTrail auditTrail,
    ICacheStore cache,
    TimeProvider timeProvider)
{
    public Task<LoanResponse> ReturnAsync(Guid loanId, CancellationToken cancellationToken = default) =>
        TransitionAsync(loanId, LoanStatus.Returned, AuditActions.LoanReturned, cancellationToken);

    public Task<LoanResponse> CancelAsync(Guid loanId, CancellationToken cancellationToken = default) =>
        TransitionAsync(loanId, LoanStatus.Cancelled, AuditActions.LoanCancelled, cancellationToken);

    private async Task<LoanResponse> TransitionAsync(
        Guid loanId,
        LoanStatus newStatus,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var (response, bookId) = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var loan = await loans.FindReadOnlyAsync(loanId, ct).ConfigureAwait(false)
                    ?? throw new DomainException(LoanErrors.NotFound, $"Emprestimo {loanId} nao encontrado.");

                var affected = await loans
                    .TryTransitionFromActiveAsync(loanId, newStatus, now, ct)
                    .ConfigureAwait(false);

                if (affected == 0)
                {
                    // Zero linhas so pode significar que ele ja nao estava ativo: o
                    // emprestimo existe (foi lido acima) e a unica outra condicao do
                    // WHERE e o status.
                    throw new DomainException(
                        LoanErrors.NotActive,
                        $"O emprestimo nao esta ativo (status atual: {loan.Status}).");
                }

                // So depois da transicao confirmada o exemplar volta a circulacao —
                // do contrario uma segunda chamada incrementaria a disponibilidade de novo.
                var liberado = await books.ReleaseCopyAsync(loan.BookId, ct).ConfigureAwait(false);

                if (liberado == 0)
                {
                    // So ocorre com a invariante total - available = ativos ja quebrada.
                    // Engolir isso commitaria um emprestimo encerrado sem devolver o
                    // exemplar ao acervo; abortar preserva a coerencia e torna o problema
                    // visivel em vez de silencioso.
                    throw new InvalidOperationException(
                        $"Invariante de disponibilidade violada no livro {loan.BookId}: " +
                        "nao ha exemplar emprestado para devolver.");
                }

                auditTrail.Record(
                    AuditEntityTypes.Loan,
                    loanId,
                    auditAction,
                    new
                    {
                        loan.BookId,
                        loan.UserId,
                        PreviousStatus = loan.Status.ToString(),
                        NewStatus = newStatus.ToString(),
                        OccurredAt = now,
                    });

                var atualizado = loan.ToResponse() with
                {
                    Status = newStatus.ToString(),
                    ReturnedAt = newStatus == LoanStatus.Returned ? now : loan.ReturnedAt,
                    CancelledAt = newStatus == LoanStatus.Cancelled ? now : loan.CancelledAt,
                };

                return (atualizado, loan.BookId);
            },
            cancellationToken).ConfigureAwait(false);

        await cache.RemoveAsync(CacheKeys.Book(bookId), cancellationToken).ConfigureAwait(false);

        return response;
    }
}
