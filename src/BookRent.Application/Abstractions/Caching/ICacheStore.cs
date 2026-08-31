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

    /// <summary>
    /// Invalidacao POS-COMMIT. Nao recebe CancellationToken de proposito: a transacao ja
    /// terminou e o dado ja mudou no banco, entao abortar a limpeza deixaria o cache
    /// mentindo pela janela inteira do TTL.
    ///
    /// Em compensacao tem teto de tempo PROPRIO, para que um Redis travado nao segure a
    /// resposta nem a conexao do pool — cache e otimizacao, e indisponibilidade dele nao
    /// pode virar indisponibilidade de escrita.
    ///
    /// NUNCA propaga excecao: a operacao de negocio ja commitou, e falhar a resposta
    /// depois disso faria o cliente acreditar que nada aconteceu. Falha aqui vira log, e
    /// o TTL cobre o resto.
    /// </summary>
    Task InvalidateAsync(string key);
}
