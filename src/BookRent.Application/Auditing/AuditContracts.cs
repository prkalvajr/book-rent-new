using BookRent.Application.Abstractions.Persistence;
using BookRent.Application.Books;

namespace BookRent.Application.Auditing;

public sealed record AuditEventResponse(
    long Id,
    string EntityType,
    Guid EntityId,
    string Action,
    string Actor,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string Data);

/// <summary>Filtros da consulta a trilha. Todos opcionais e combinaveis.</summary>
public sealed record SearchAuditEventsQuery(
    string? EntityType,
    Guid? EntityId,
    string? Action,
    string? Actor,
    string? CorrelationId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize)
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    public SearchAuditEventsQuery Normalized() => this with
    {
        Page = Page < 1 ? 1 : Page,
        PageSize = PageSize switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => PageSize,
        },
    };
}

/// <summary>
/// Leitura da trilha de auditoria. Somente consulta: a tabela e append-only e nao ha
/// caso de uso que altere ou remova evento.
/// </summary>
public sealed class SearchAuditEventsHandler(IAuditEventRepository auditEvents)
{
    public Task<PagedResult<AuditEventResponse>> HandleAsync(
        SearchAuditEventsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return auditEvents.SearchAsync(query.Normalized(), cancellationToken);
    }
}
