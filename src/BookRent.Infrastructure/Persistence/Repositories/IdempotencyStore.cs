using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace BookRent.Infrastructure.Persistence.Repositories;

internal sealed class IdempotencyStore(BookRentDbContext dbContext) : IIdempotencyStore
{
    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(
        IdempotencyRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // SQL explicito porque o change tracker nao expressa ON CONFLICT DO NOTHING.
        // A alternativa — tentar inserir e capturar a violacao — nao serve no PostgreSQL:
        // qualquer erro aborta a transacao inteira, e recuperar exigiria SAVEPOINT.
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO bookrent.idempotency_records
                 (endpoint, key, request_hash, created_at, expires_at)
             VALUES
                 ({record.Endpoint}, {record.Key}, {record.RequestHash}, {record.CreatedAt}, {record.ExpiresAt})
             ON CONFLICT (endpoint, key) DO NOTHING
             """,
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    public Task<IdempotencyRecord?> FindAsync(
        string endpoint,
        string key,
        CancellationToken cancellationToken = default) =>
        dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.Endpoint == endpoint && record.Key == key, cancellationToken);

    public Task CompleteAsync(
        string endpoint,
        string key,
        int responseStatus,
        string responseBody,
        Guid? loanId,
        CancellationToken cancellationToken = default) =>
        dbContext.IdempotencyRecords
            .Where(record => record.Endpoint == endpoint && record.Key == key)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.ResponseStatus, responseStatus)
                    .SetProperty(record => record.ResponseBody, responseBody)
                    .SetProperty(record => record.LoanId, loanId),
                cancellationToken);
}
