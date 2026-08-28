using BookRent.Application.Books;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace BookRent.Api.Endpoints;

/// <summary>Endpoints do catalogo.</summary>
internal static class BookEndpoints
{
    public static IEndpointRouteBuilder MapBookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var books = endpoints.MapGroup("/books").WithTags("Livros");

        books.MapPost("/", CreateAsync)
            .WithSummary("Cadastra um livro no catalogo")
            .Produces<BookResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        books.MapGet("/", SearchAsync)
            .WithSummary("Lista o catalogo, com busca por titulo ou autor")
            .Produces<PagedResult<BookResponse>>();

        books.MapGet("/{id:guid}", GetAsync)
            .WithSummary("Consulta um livro")
            .Produces<BookResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        books.MapPatch("/{id:guid}", UpdateAsync)
            .WithSummary("Altera um livro (concorrencia otimista via ExpectedVersion)")
            .Produces<BookResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        books.MapDelete("/{id:guid}", DeactivateAsync)
            .WithSummary("Desativa um livro; nunca apaga o registro nem o historico")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<Created<BookResponse>> CreateAsync(
        CreateBookRequest request,
        CreateBookHandler handler,
        CancellationToken cancellationToken)
    {
        var book = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return TypedResults.Created($"/books/{book.Id}", book);
    }

    private static async Task<Ok<PagedResult<BookResponse>>> SearchAsync(
        SearchBooksHandler handler,
        CancellationToken cancellationToken,
        [FromQuery] string? q = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = SearchBooksQuery.DefaultPageSize)
    {
        var result = await handler
            .HandleAsync(new SearchBooksQuery(q, includeInactive, page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(result);
    }

    private static async Task<Ok<BookResponse>> GetAsync(
        Guid id,
        GetBookHandler handler,
        CancellationToken cancellationToken)
    {
        var book = await handler.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(book);
    }

    private static async Task<Ok<BookResponse>> UpdateAsync(
        Guid id,
        UpdateBookRequest request,
        UpdateBookHandler handler,
        CancellationToken cancellationToken)
    {
        var book = await handler.HandleAsync(id, request, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(book);
    }

    private static async Task<NoContent> DeactivateAsync(
        Guid id,
        DeactivateBookHandler handler,
        CancellationToken cancellationToken)
    {
        await handler.HandleAsync(id, cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }
}
