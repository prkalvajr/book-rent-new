using System.Data;
using BookRent.Application.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BookRent.Infrastructure.Persistence;

/// <inheritdoc cref="IUnitOfWork" />
internal sealed class UnitOfWork(BookRentDbContext dbContext) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Usa a execution strategy do Npgsql (retry para falhas transitorias), por isso
    /// a transacao e aberta DENTRO do delegate — reexecutar exige recomecar a transacao.
    /// </summary>
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            cancellationToken,
            async (ct) =>
            {
                await using var transaction = await dbContext.Database
                    .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                    .ConfigureAwait(false);

                var result = await operation(ct).ConfigureAwait(false);

                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return result;
            }).ConfigureAwait(false);
    }
}
