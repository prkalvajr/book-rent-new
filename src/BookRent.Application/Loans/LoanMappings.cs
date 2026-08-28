using BookRent.Domain.Loans;

namespace BookRent.Application.Loans;

internal static class LoanMappings
{
    public static LoanResponse ToResponse(this Loan loan)
    {
        ArgumentNullException.ThrowIfNull(loan);

        return new LoanResponse(
            loan.Id,
            loan.BookId,
            loan.UserId,
            loan.Status.ToString(),
            loan.LoanedAt,
            loan.DueAt,
            loan.ReturnedAt,
            loan.CancelledAt,
            loan.Actor);
    }
}
