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
///
/// Cuidado com o filtro dos catch. A versao anterior era
/// <c>when (ex is not OperationCanceledException)</c>, partindo da premissa de que essa
/// excecao so poderia vir do chamador. E FALSO: com o Redis travado, o
/// StackExchange.Redis cancela as mensagens que ficaram em backlog e o
/// <c>RedisCache</c> lanca <c>TaskCanceledException</c> — que herda de
/// <c>OperationCanceledException</c> — mesmo recebendo <c>CancellationToken.None</c>.
/// Ela escapava, nenhum IExceptionHandler a reconhecia, e uma escrita ja COMMITADA
/// respondia 500. O criterio correto e o estado do token, nao o tipo da excecao.
/// </summary>
internal sealed partial class RedisCacheStore(
    IDistributedCache cache,
    IOptions<CacheOptions> options,
    ILogger<RedisCacheStore> logger) : ICacheStore
{
    /// <summary>
    /// Teto da invalidacao pos-commit. Curto de proposito: com o pool dimensionado em 8
    /// conexoes por replica, segurar a resposta por segundos apos o commit esgota o pool
    /// e transforma queda de cache em indisponibilidade de escrita.
    /// </summary>
    private static readonly TimeSpan InvalidationTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly CacheOptions _options = options.Value;

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);

            return payload is null ? default : JsonSerializer.Deserialize<T>(payload, SerializerOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWriteFailure(logger, key, ex);
        }
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(string key)
    {
        // Limitar por CancellationToken NAO funciona aqui: o StackExchange.Redis nao
        // aborta um comando ja despachado, entao a chamada so retorna quando o
        // asyncTimeout dele (5 s por padrao) estoura. Medido: quatro escritas levavam
        // ~21 s com o Redis travado.
        //
        // A saida e parar de ESPERAR em vez de tentar cancelar. A remocao continua em
        // background e a resposta segue.
        var remocao = RemoverSemPropagarAsync(key);
        var concluida = await Task
            .WhenAny(remocao, Task.Delay(InvalidationTimeout))
            .ConfigureAwait(false);

        if (concluida != remocao)
        {
            // A tarefa orfa e segura: RemoverSemPropagarAsync nunca lanca, entao nao
            // deixa excecao nao observada. Se ela concluir depois, otimo; se nao, o TTL
            // cobre a divergencia — que e exatamente o papel dele.
            LogInvalidationTimeout(logger, key, InvalidationTimeout.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Remove capturando tudo, inclusive cancelamento: nao ha chamador a quem propagar,
    /// porque a operacao de negocio ja commitou. E o que torna a tarefa orfa segura.
    /// </summary>
    private async Task RemoverSemPropagarAsync(string key)
    {
        try
        {
            await cache.RemoveAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInvalidationFailure(logger, key, ex);
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
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Invalidacao da chave {CacheKey} excedeu {TimeoutMs} ms; segue em background e o TTL cobre a divergencia")]
    private static partial void LogInvalidationTimeout(ILogger logger, string cacheKey, double timeoutMs);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Falha ao invalidar a chave {CacheKey} no Redis; o TTL cobre a divergencia")]
    private static partial void LogInvalidationFailure(ILogger logger, string cacheKey, Exception exception);
}
