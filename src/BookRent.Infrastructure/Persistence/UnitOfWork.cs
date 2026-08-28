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
                // Cada tentativa comeca do zero. O ExecutionStrategy reexecuta este
                // delegate no MESMO DbContext, e o rollback desfaz a transacao no banco
                // mas NAO limpa o ChangeTracker: entidades adicionadas na tentativa
                // anterior continuariam em estado Added e seriam gravadas junto com as
                // da nova tentativa.
                //
                // Sem isto, um erro transitorio no SaveChanges — 53300
                // too_many_connections, por exemplo, que e justamente a falha esperada
                // sob contencao (secao 9.12) — produziria DOIS emprestimos com UM unico
                // decremento de disponibilidade. A CHECK constraint nao pega esse caso:
                // ela limita available_copies entre 0 e total_copies, e o numero
                // continuaria dentro da faixa enquanto a invariante
                // "total - available = emprestimos ativos" ja estaria quebrada.
                dbContext.ChangeTracker.Clear();

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
