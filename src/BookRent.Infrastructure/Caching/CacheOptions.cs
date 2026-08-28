namespace BookRent.Infrastructure.Caching;

/// <summary>Configuracao do cache, vinda da secao <c>Cache</c> do appsettings.</summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>TTL aplicado quando a chamada nao informa um explicito.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Prefixo das chaves no Redis, permitindo compartilhar a instancia.</summary>
    public string InstanceName { get; set; } = "bookrent:";
}
