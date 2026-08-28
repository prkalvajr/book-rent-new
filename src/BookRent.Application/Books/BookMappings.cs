using BookRent.Domain.Books;

namespace BookRent.Application.Books;

internal static class BookMappings
{
    public static BookResponse ToResponse(this Book book)
    {
        ArgumentNullException.ThrowIfNull(book);

        return new BookResponse(
            book.Id,
            book.Title,
            book.Isbn,
            book.Author,
            book.TotalCopies,
            book.AvailableCopies,
            book.ActiveLoans,
            book.IsActive,
            book.Version,
            book.CreatedAt,
            book.UpdatedAt,
            book.DeactivatedAt);
    }
}
