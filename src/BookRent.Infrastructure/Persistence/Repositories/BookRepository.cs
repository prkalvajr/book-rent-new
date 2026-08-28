using BookRent.Application.Abstractions.Persistence;
using BookRent.Application.Books;
using BookRent.Domain.Books;
using BookRent.Domain.Loans;
using Microsoft.EntityFrameworkCore;

namespace BookRent.Infrastructure.Persistence.Repositories;

internal sealed class BookRepository(BookRentDbContext dbContext) : IBookRepository
{
    public void Add(Book book) => dbContext.Books.Add(book);

    public Task<Book?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Books.FirstOrDefaultAsync(book => book.Id == id, cancellationToken);

    public Task<Book?> FindReadOnlyAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Books.AsNoTracking().FirstOrDefaultAsync(book => book.Id == id, cancellationToken);

    public Task<bool> IsbnExistsAsync(
        string isbn,
        Guid? excludingBookId,
        CancellationToken cancellationToken = default) =>
        dbContext.Books
            .AsNoTracking()
            .AnyAsync(
                book => book.Isbn == isbn && (excludingBookId == null || book.Id != excludingBookId),
                cancellationToken);

    /// <summary>Consulta servida pelo indice parcial <c>ix_loans_active_by_book</c>.</summary>
    public Task<bool> HasActiveLoansAsync(Guid bookId, CancellationToken cancellationToken = default) =>
        dbContext.Loans
            .AsNoTracking()
            .AnyAsync(loan => loan.BookId == bookId && loan.Status == LoanStatus.Active, cancellationToken);

    public async Task<PagedResult<BookResponse>> SearchAsync(
        SearchBooksQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = dbContext.Books.AsNoTracking();

        if (!query.IncludeInactive)
        {
            source = source.Where(book => book.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            // ILIKE com curinga nos dois lados: e o que os indices GIN de trigrama
            // (ix_books_title_trgm / ix_books_author_trgm) foram criados para atender.
            var pattern = $"%{query.Query.Trim()}%";

            source = source.Where(book =>
                EF.Functions.ILike(book.Title, pattern) || EF.Functions.ILike(book.Author, pattern));
        }

        var total = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var items = await source
            .OrderBy(book => book.Title)
            .ThenBy(book => book.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(book => new BookResponse(
                book.Id,
                book.Title,
                book.Isbn,
                book.Author,
                book.TotalCopies,
                book.AvailableCopies,
                book.TotalCopies - book.AvailableCopies,
                book.IsActive,
                book.Version,
                book.CreatedAt,
                book.UpdatedAt,
                book.DeactivatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<BookResponse>(items, query.Page, query.PageSize, total);
    }

    /// <inheritdoc />
    public Task<int> ApplyCatalogUpdateAsync(
        Book book,
        int expectedVersion,
        int availabilityDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);

        return dbContext.Books
            .Where(candidate =>
                candidate.Id == book.Id
                && candidate.Version == expectedVersion
                && candidate.IsActive
                && candidate.AvailableCopies + availabilityDelta >= 0)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Title, book.Title)
                    .SetProperty(candidate => candidate.Isbn, book.Isbn)
                    .SetProperty(candidate => candidate.Author, book.Author)
                    .SetProperty(candidate => candidate.TotalCopies, book.TotalCopies)
                    // Relativa, e nao absoluta: um emprestimo concorrente pode ter mudado
                    // a disponibilidade sem tocar em version.
                    .SetProperty(
                        candidate => candidate.AvailableCopies,
                        candidate => candidate.AvailableCopies + availabilityDelta)
                    .SetProperty(candidate => candidate.Version, expectedVersion + 1)
                    .SetProperty(candidate => candidate.UpdatedAt, book.UpdatedAt),
                cancellationToken);
    }
}
