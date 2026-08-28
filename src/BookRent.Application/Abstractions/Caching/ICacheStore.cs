namespace BookRent.Application.Abstractions.Caching;

/// <summary>
/// Porta de cache distribuido (implementada com Redis).
/// O PostgreSQL continua sendo a fonte de verdade: o cache serve apenas leituras,
/// nunca decisoes de disponibilidade ou criacao de emprestimo.
/// </summary>
public interface ICacheStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Invalida um conjunto de chaves apos uma escrita que afeta leituras cacheadas.</summary>
    Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);
}
