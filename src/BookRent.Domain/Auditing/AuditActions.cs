namespace BookRent.Domain.Auditing;

/// <summary>
/// Acoes registradas na trilha de auditoria. Sao nomes de INTENCAO DE NEGOCIO,
/// nao de mudanca de linha: "emprestimo criado" carrega o porque que um diff de
/// colunas ("available_copies foi de 1 para 0") nao carrega.
/// </summary>
public static class AuditActions
{
    public const string BookCreated = "book.created";
    public const string BookUpdated = "book.updated";
    public const string BookDeactivated = "book.deactivated";
    public const string LoanCreated = "loan.created";
    public const string LoanReturned = "loan.returned";
    public const string LoanCancelled = "loan.cancelled";
}
