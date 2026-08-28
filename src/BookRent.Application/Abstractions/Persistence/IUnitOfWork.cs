namespace BookRent.Application.Abstractions.Persistence;

/// <summary>
/// Fronteira transacional da camada de aplicacao.
/// Implementado pela infraestrutura sobre o <c>DbContext</c> do EF Core.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executa <paramref name="operation"/> dentro de uma transacao, com retry
    /// para falhas transitorias e conflitos de concorrencia otimista.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
