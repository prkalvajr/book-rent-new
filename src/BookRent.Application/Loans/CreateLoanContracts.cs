namespace BookRent.Application.Loans;

public sealed record CreateLoanRequest(Guid BookId, Guid UserId);

/// <summary>
/// <paramref name="Replayed"/> indica que a requisicao reaproveitou uma resposta ja
/// produzida por uma chamada anterior com a mesma <c>Idempotency-Key</c> — nenhum
/// emprestimo novo foi criado e a disponibilidade nao foi decrementada de novo.
/// </summary>
public sealed record CreateLoanResult(LoanResponse Loan, bool Replayed);

/// <summary>Configuracao da secao <c>Loans</c> do appsettings.</summary>
public sealed class LoanOptions
{
    public const string SectionName = "Loans";

    public int DefaultLoanPeriodDays { get; set; } = 14;

    /// <summary>
    /// Por quanto tempo uma <c>Idempotency-Key</c> continua valendo para replay.
    /// Expirado, o registro fica elegivel a expurgo — que hoje nao existe (limitacao conhecida).
    /// </summary>
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan LoanPeriod => TimeSpan.FromDays(DefaultLoanPeriodDays);
}
