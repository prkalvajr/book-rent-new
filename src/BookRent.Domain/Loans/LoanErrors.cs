namespace BookRent.Domain.Loans;

/// <summary>Codigos estaveis das regras de negocio de emprestimo.</summary>
public static class LoanErrors
{
    public const string NotFound = "loan.not_found";
    public const string NoCopiesAvailable = "loan.no_copies_available";
    public const string NotActive = "loan.not_active";
    public const string ActorRequired = "loan.actor_required";
    public const string InvalidLoanPeriod = "loan.invalid_period";
    public const string IdempotencyKeyRequired = "loan.idempotency_key_required";
    public const string IdempotencyKeyReused = "loan.idempotency_key_reused";
}
