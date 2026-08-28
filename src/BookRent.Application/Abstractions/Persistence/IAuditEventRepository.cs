using BookRent.Application.Auditing;
using BookRent.Application.Books;

namespace BookRent.Application.Abstractions.Persistence;

public interface IAuditEventRepository
{
    Task<PagedResult<AuditEventResponse>> SearchAsync(
        SearchAuditEventsQuery query,
        CancellationToken cancellationToken = default);
}
