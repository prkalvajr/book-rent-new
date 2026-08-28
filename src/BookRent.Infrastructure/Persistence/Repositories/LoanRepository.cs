using BookRent.Application.Abstractions.Persistence;
using BookRent.Application.Books;
using BookRent.Application.Loans;
using BookRent.Domain.Loans;
using Microsoft.EntityFrameworkCore;

namespace BookRent.Infrastructure.Persistence.Repositories;

internal sealed class LoanRepository(BookRentDbContext dbContext) : ILoanRepository
{
    public void Add(Loan loan) => dbContext.Loans.Add(loan);

    public Task<Loan?> FindReadOnlyAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Loans.AsNoTracking().FirstOrDefaultAsync(loan => loan.Id == id, cancellationToken);

    public async Task<PagedResult<LoanResponse>> SearchAsync(
        SearchLoansQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = dbContext.Loans.AsNoTracking();

        if (query.UserId is { } userId)
        {
            source = source.Where(loan => loan.UserId == userId);
        }

        if (query.BookId is { } bookId)
        {
            source = source.Where(loan => loan.BookId == bookId);
        }

        if (query.Status is { } status)
        {
            source = source.Where(loan => loan.Status == status);
        }

        var total = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Ordenacao alinhada aos indices ix_loans_user_loaned_at / ix_loans_book_loaned_at.
        var items = await source
            .OrderByDescending(loan => loan.LoanedAt)
            .ThenBy(loan => loan.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(loan => new LoanResponse(
                loan.Id,
                loan.BookId,
                loan.UserId,
                loan.Status.ToString(),
                loan.LoanedAt,
                loan.DueAt,
                loan.ReturnedAt,
                loan.CancelledAt,
                loan.Actor))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<LoanResponse>(items, query.Page, query.PageSize, total);
    }

    /// <inheritdoc />
    public Task<int> TryTransitionFromActiveAsync(
        Guid loanId,
        LoanStatus newStatus,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var returnedAt = newStatus == LoanStatus.Returned ? occurredAt : (DateTimeOffset?)null;
        var cancelledAt = newStatus == LoanStatus.Cancelled ? occurredAt : (DateTimeOffset?)null;

        return dbContext.Loans
            .Where(loan => loan.Id == loanId && loan.Status == LoanStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(loan => loan.Status, newStatus)
                    .SetProperty(loan => loan.ReturnedAt, returnedAt)
                    .SetProperty(loan => loan.CancelledAt, cancelledAt),
                cancellationToken);
    }
}
