using BookRent.Application.Abstractions.Caching;
using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Books;
using BookRent.Domain.Common;

namespace BookRent.Application.Books;

/// <summary>
/// Leitura do livro com cache-aside. Unico ponto que popula <c>bookrent:book:{id}</c>,
/// e por isso tambem serve a consulta de disponibilidade — os dois endpoints leem os
/// mesmos bytes e nao conseguem divergir.
/// </summary>
public sealed class GetBookHandler(IBookRepository books, ICacheStore cache)
{
    /// <summary>TTL curto: e tambem o tempo maximo de divergencia se uma invalidacao se perder.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public async Task<BookResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var key = CacheKeys.Book(id);

        var cached = await cache.GetAsync<BookResponse>(key, cancellationToken).ConfigureAwait(false);

        if (cached is not null)
        {
            return cached;
        }

        var book = await books.FindReadOnlyAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(BookErrors.NotFound, $"Livro {id} nao encontrado.");

        var response = book.ToResponse();

        await cache.SetAsync(key, response, CacheTtl, cancellationToken).ConfigureAwait(false);

        return response;
    }
}
