namespace BookRent.Application.Abstractions.Caching;

/// <summary>
/// Fonte unica das chaves de cache. Centralizar aqui evita que uma escrita
/// invalide uma chave com formato diferente da usada na leitura.
/// A listagem GET /books NAO e cacheada de proposito — qualquer emprestimo
/// invalidaria todas as paginas e a taxa de acerto ficaria proxima de zero.
/// </summary>
public static class CacheKeys
{
    // Sem prefixo proprio: quem namespaceia as chaves e o InstanceName do
    // IDistributedCache (Cache:InstanceName), aplicado pelo adaptador do Redis. Um
    // prefixo aqui produzia a chave duplicada "bookrent:bookrent:book:{id}".

    /// <summary>
    /// Snapshot completo do livro. Chave unica do cache: serve GET /books/{id} e
    /// GET /books/{id}/availability, que projeta os dois numeros do mesmo valor.
    /// Nao ha chave separada para disponibilidade — qualquer miss le a linha inteira
    /// do livro de qualquer forma, entao separar nao economizaria consulta nenhuma.
    /// </summary>
    public static string Book(Guid bookId) => $"book:{bookId}";
}
