using BookRent.Application.Abstractions.Auditing;
using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Auditing;
using BookRent.Domain.Books;
using BookRent.Domain.Common;

namespace BookRent.Application.Books;

/// <summary>Cria um livro no catalogo, com ISBN unico e trilha de auditoria.</summary>
public sealed class CreateBookHandler(
    IBookRepository books,
    IUnitOfWork unitOfWork,
    IAuditTrail auditTrail,
    TimeProvider timeProvider)
{
    public async Task<BookResponse> HandleAsync(CreateBookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = timeProvider.GetUtcNow();

        // O dominio valida e normaliza (inclusive o ISBN) antes de qualquer consulta:
        // nao faz sentido perguntar ao banco por um ISBN mal formado.
        var book = Book.Create(request.Title, request.Isbn, request.Author, request.TotalCopies, now);

        var created = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                // Checagem amigavel; a garantia real e o indice unico ux_books_isbn,
                // que o adaptador traduz em conflito caso duas criacoes corram juntas.
                if (await books.IsbnExistsAsync(book.Isbn, excludingBookId: null, ct).ConfigureAwait(false))
                {
                    throw new DomainException(
                        BookErrors.IsbnAlreadyExists,
                        $"Ja existe um livro com o ISBN {book.Isbn}.");
                }

                books.Add(book);

                auditTrail.Record(
                    AuditEntityTypes.Book,
                    book.Id,
                    AuditActions.BookCreated,
                    new
                    {
                        book.Title,
                        book.Isbn,
                        book.Author,
                        book.TotalCopies,
                    });

                return book;
            },
            cancellationToken).ConfigureAwait(false);

        return created.ToResponse();
    }
}
