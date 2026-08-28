using System.Text.Json;
using BookRent.Application.Abstractions.Auditing;
using BookRent.Application.Abstractions.Correlation;
using BookRent.Domain.Auditing;
using BookRent.Infrastructure.Persistence;

namespace BookRent.Infrastructure.Auditing;

/// <summary>
/// Adaptador da trilha de auditoria sobre o EF Core.
///
/// Apenas adiciona o evento ao contexto: a gravacao acontece no SaveChanges da MESMA
/// transacao da mudanca. Ou os dois existem, ou nenhum — a trilha nunca fica dessincronizada
/// do fato que ela descreve.
/// </summary>
internal sealed class AuditTrail(
    BookRentDbContext dbContext,
    ICorrelationContext correlationContext,
    TimeProvider timeProvider) : IAuditTrail
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public void Record(string entityType, Guid entityId, string action, object data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var auditEvent = AuditEvent.Record(
            entityType,
            entityId,
            action,
            correlationContext.Actor,
            correlationContext.CorrelationId,
            JsonSerializer.Serialize(data, SerializerOptions),
            timeProvider.GetUtcNow());

        dbContext.AuditEvents.Add(auditEvent);
    }
}
