using System.Text.Json;
using BookRent.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookRent.Infrastructure.Caching;

/// <summary>
/// Adaptador de <see cref="ICacheStore"/> sobre Redis.
/// Falha de cache e degradacao, nao erro: a operacao apenas registra log e devolve
/// o controle para a fonte de verdade (PostgreSQL).
/// </summary>
internal sealed partial class RedisCacheStore(
    IDistributedCache cache,
    IOptions<CacheOptions> options,
    ILogger<RedisCacheStore> logger) : ICacheStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly CacheOptions _options = options.Value;

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);

            return payload is null ? default : JsonSerializer.Deserialize<T>(payload, SerializerOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReadFailure(logger, key, ex);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var entryOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl ?? _options.DefaultTtl,
            };

            var payload = JsonSerializer.Serialize(value, SerializerOptions);
            await cache.SetStringAsync(key, payload, entryOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWriteFailure(logger, key, ex);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInvalidationFailure(logger, key, ex);
        }
    }

    public async Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        foreach (var key in keys)
        {
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Falha ao ler a chave {CacheKey} do Redis; seguindo para a fonte de verdade")]
    private static partial void LogReadFailure(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Falha ao gravar a chave {CacheKey} no Redis")]
    private static partial void LogWriteFailure(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Falha ao invalidar a chave {CacheKey} no Redis")]
    private static partial void LogInvalidationFailure(ILogger logger, string cacheKey, Exception exception);
}
