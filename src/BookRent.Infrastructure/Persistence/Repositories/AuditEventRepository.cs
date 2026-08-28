using BookRent.Application.Abstractions.Persistence;
using BookRent.Application.Auditing;
using BookRent.Application.Books;
using Microsoft.EntityFrameworkCore;

namespace BookRent.Infrastructure.Persistence.Repositories;

internal sealed class AuditEventRepository(BookRentDbContext dbContext) : IAuditEventRepository
{
    public async Task<PagedResult<AuditEventResponse>> SearchAsync(
        SearchAuditEventsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = dbContext.AuditEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            source = source.Where(evento => evento.EntityType == query.EntityType);
        }

        if (query.EntityId is { } entityId)
        {
            source = source.Where(evento => evento.EntityId == entityId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            source = source.Where(evento => evento.Action == query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            source = source.Where(evento => evento.Actor == query.Actor);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            source = source.Where(evento => evento.CorrelationId == query.CorrelationId);
        }

        if (query.From is { } from)
        {
            source = source.Where(evento => evento.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(evento => evento.OccurredAt <= to);
        }

        var total = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Ordenacao alinhada a ix_audit_events_occurred_at; o id desempata eventos
        // gravados no mesmo instante, preservando a ordem real de insercao.
        var items = await source
            .OrderByDescending(evento => evento.OccurredAt)
            .ThenByDescending(evento => evento.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(evento => new AuditEventResponse(
                evento.Id,
                evento.EntityType,
                evento.EntityId,
                evento.Action,
                evento.Actor,
                evento.OccurredAt,
                evento.CorrelationId,
                evento.Data))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<AuditEventResponse>(items, query.Page, query.PageSize, total);
    }
}
