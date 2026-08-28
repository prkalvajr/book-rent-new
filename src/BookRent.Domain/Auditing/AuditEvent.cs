using BookRent.Domain.Common;

namespace BookRent.Domain.Auditing;

/// <summary>
/// Evento da trilha de auditoria de negocio. Tabela append-only: nunca sofre
/// UPDATE nem DELETE, e o DbContext bloqueia os dois.
///
/// Nao herda de <see cref="Entity"/> porque a chave e <c>bigint identity</c>, e nao
/// um Guid: o id nunca aparece em URL nem e chave estrangeira, entao pagar 16 bytes
/// por linha na tabela que mais cresce seria custo sem contrapartida (secao 9.6).
///
/// E gravado na MESMA transacao da mudanca que descreve — a trilha nao pode divergir
/// do fato que ela registra.
/// </summary>
public sealed class AuditEvent
{
    public const int MaxEntityTypeLength = 50;
    public const int MaxActionLength = 50;
    public const int MaxActorLength = 200;
    public const int MaxCorrelationIdLength = 100;

    /// <summary>Construtor exigido pelo materializador do EF Core.</summary>
    private AuditEvent()
    {
    }

    private AuditEvent(
        string entityType,
        Guid entityId,
        string action,
        string actor,
        string correlationId,
        string data,
        DateTimeOffset occurredAt)
    {
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        Actor = actor;
        CorrelationId = correlationId;
        Data = data;
        OccurredAt = occurredAt;
    }

    /// <summary>Sequencial do banco: cresce com a tabela, sem custo de 16 bytes por linha.</summary>
    public long Id { get; private set; }

    public string EntityType { get; private set; } = null!;

    public Guid EntityId { get; private set; }

    public string Action { get; private set; } = null!;

    /// <summary>Quem originou a operacao (cabecalho X-Actor-Id).</summary>
    public string Actor { get; private set; } = null!;

    /// <summary>Sempre em UTC, normalizado na criacao.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Mesmo identificador que aparece nos logs estruturados e nos traces.</summary>
    public string CorrelationId { get; private set; } = null!;

    /// <summary>JSON com o suficiente para entender a mudanca. Mapeado para jsonb.</summary>
    public string Data { get; private set; } = null!;

    public static AuditEvent Record(
        string entityType,
        Guid entityId,
        string action,
        string? actor,
        string? correlationId,
        string data,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        ArgumentOutOfRangeException.ThrowIfEqual(entityId, Guid.Empty);

        return new AuditEvent(
            entityType,
            entityId,
            action,
            Truncate(actor, MaxActorLength, "anonymous"),
            Truncate(correlationId, MaxCorrelationIdLength, "unknown"),
            data,
            occurredAt.ToUniversalTime());
    }

    private static string Truncate(string? value, int maxLength, string fallback)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return fallback;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
