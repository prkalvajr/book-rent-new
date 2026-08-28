namespace BookRent.Domain.Loans;

/// <summary>
/// Estados possiveis de um emprestimo. Persistido como TEXTO no PostgreSQL:
/// um dump continua legivel e renumerar o enum nao corrompe dados em silencio.
/// </summary>
public enum LoanStatus
{
    /// <summary>Exemplar esta com o leitor.</summary>
    Active,

    /// <summary>Exemplar foi devolvido. O registro permanece no historico.</summary>
    Returned,

    /// <summary>Emprestimo cancelado. O registro permanece no historico.</summary>
    Cancelled,
}
