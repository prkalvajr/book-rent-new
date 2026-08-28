using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BookRent.Infrastructure.Health;

/// <summary>
/// Check de readiness do Redis. Escrito a mao (em vez de um pacote de terceiros)
/// para nao arrastar uma versao divergente do StackExchange.Redis para o grafo.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer multiplexer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await multiplexer.GetDatabase().PingAsync().ConfigureAwait(false);

            return HealthCheckResult.Healthy(
                "Redis respondeu ao PING",
                new Dictionary<string, object> { ["latencyMs"] = latency.TotalMilliseconds });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis indisponivel", ex);
        }
    }
}
