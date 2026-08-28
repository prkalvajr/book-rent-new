namespace BookRent.Application.Abstractions.Auditing;

/// <summary>
/// Porta da trilha de auditoria de negocio.
///
/// O evento e registrado pelo caso de uso, explicitamente, e persistido na MESMA
/// transacao da mudanca que ele descreve — a trilha nao pode divergir do fato.
/// A alternativa (interceptor lendo o ChangeTracker) registraria mudanca de linha,
/// nao intencao de negocio. Ver secao 4 do plano.
///
/// Ator, correlationId e timestamp UTC sao preenchidos pelo adaptador a partir do
/// contexto da requisicao: o caso de uso descreve O QUE mudou, nao quem nem quando.
/// </summary>
public interface IAuditTrail
{
    /// <param name="entityType">Ver <c>AuditEntityTypes</c>.</param>
    /// <param name="action">Ver <c>AuditActions</c>.</param>
    /// <param name="data">Objeto serializado para jsonb com o suficiente para entender a mudanca.</param>
    void Record(string entityType, Guid entityId, string action, object data);
}
