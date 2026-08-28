using BookRent.Domain.Common;

namespace BookRent.Domain.Loans;

/// <summary>
/// Emprestimo de um exemplar. Raiz de agregado e registro permanente:
/// devolucao e cancelamento mudam o <see cref="Status"/>, nunca apagam a linha.
///
/// A maquina de estados vive aqui; sob concorrencia ela e garantida tambem por um
/// UPDATE condicional (<c>WHERE status = 'Active'</c>) no caminho de persistencia —
/// mesma divisao de papeis do contador de exemplares: o dominio expressa a regra,
/// o banco a garante.
/// </summary>
public sealed class Loan : Entity, IAggregateRoot
{
    public const int MaxActorLength = 200;

    /// <summary>Construtor exigido pelo materializador do EF Core.</summary>
    private Loan()
    {
    }

    private Loan(Guid bookId, Guid userId, string actor, DateTimeOffset loanedAt, DateTimeOffset dueAt)
        : base(Guid.CreateVersion7())
    {
        BookId = bookId;
        UserId = userId;
        Actor = actor;
        LoanedAt = loanedAt;
        DueAt = dueAt;
        Status = LoanStatus.Active;
    }

    public Guid BookId { get; private set; }

    public Guid UserId { get; private set; }

    public LoanStatus Status { get; private set; }

    public DateTimeOffset LoanedAt { get; private set; }

    /// <summary>Data prevista de devolucao: <see cref="LoanedAt"/> mais o periodo configurado.</summary>
    public DateTimeOffset DueAt { get; private set; }

    public DateTimeOffset? ReturnedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Quem originou a operacao (cabecalho X-Actor-Id), preservado na trilha.</summary>
    public string Actor { get; private set; } = null!;

    public bool IsActive => Status == LoanStatus.Active;

    public static Loan Create(Guid bookId, Guid userId, string? actor, DateTimeOffset now, TimeSpan loanPeriod)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(bookId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);

        if (loanPeriod <= TimeSpan.Zero)
        {
            throw new DomainException(LoanErrors.InvalidLoanPeriod, "O periodo de emprestimo deve ser positivo.");
        }

        var trimmedActor = actor?.Trim();

        if (string.IsNullOrEmpty(trimmedActor) || trimmedActor.Length > MaxActorLength)
        {
            throw new DomainException(LoanErrors.ActorRequired, "O ator da operacao e obrigatorio.");
        }

        return new Loan(bookId, userId, trimmedActor, now, now + loanPeriod);
    }

    /// <summary>Registra a devolucao. O emprestimo continua no historico, com outro status.</summary>
    public void Return(DateTimeOffset now)
    {
        EnsureActive();

        Status = LoanStatus.Returned;
        ReturnedAt = now;
    }

    /// <summary>Cancela o emprestimo, preservando a informacao de que ele existiu.</summary>
    public void Cancel(DateTimeOffset now)
    {
        EnsureActive();

        Status = LoanStatus.Cancelled;
        CancelledAt = now;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new DomainException(
                LoanErrors.NotActive,
                $"O emprestimo nao esta ativo (status atual: {Status}).");
        }
    }
}
