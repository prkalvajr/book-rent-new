namespace BookRent.Infrastructure.Caching;

/// <summary>Configuracao do cache, vinda da secao <c>Cache</c> do appsettings.</summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// TTL das entradas de cache. Curto de proposito: e tambem o tempo maximo de
    /// divergencia caso uma invalidacao se perca (processo morto entre o COMMIT e o DEL,
    /// ou Redis fora), porque nada alem da expiracao conserta isso.
    /// </summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Prefixo das chaves no Redis, permitindo compartilhar a instancia.</summary>
    public string InstanceName { get; set; } = "bookrent:";
}
