using BookRent.Application.Books;
using BookRent.Application.Loans;
using BookRent.Application.Users;
using BookRent.Domain.Loans;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace BookRent.Api.Endpoints;

/// <summary>Endpoints de leitores e do historico de emprestimos deles.</summary>
internal static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var users = endpoints.MapGroup("/users").WithTags("Leitores");

        users.MapPost("/", RegisterAsync)
            .WithSummary("Cadastra um leitor")
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Nao esta na lista minima do desafio; entra porque POST devolve Location
        // apontando para ca, e um Location que responde 404 seria um contrato quebrado.
        users.MapGet("/{id:guid}", GetAsync)
            .WithSummary("Consulta um leitor")
            .Produces<UserResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapGet("/{id:guid}/loans", GetLoansAsync)
            .WithSummary("Historico de emprestimos do leitor, em qualquer estado")
            .Produces<PagedResult<LoanResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<Created<UserResponse>> RegisterAsync(
        RegisterUserRequest request,
        RegisterUserHandler handler,
        CancellationToken cancellationToken)
    {
        var user = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return TypedResults.Created($"/users/{user.Id}", user);
    }

    private static async Task<Ok<UserResponse>> GetAsync(
        Guid id,
        GetUserHandler handler,
        CancellationToken cancellationToken)
    {
        var user = await handler.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(user);
    }

    private static async Task<Ok<PagedResult<LoanResponse>>> GetLoansAsync(
        Guid id,
        GetUserLoansHandler handler,
        CancellationToken cancellationToken,
        [FromQuery] LoanStatus? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = SearchLoansQuery.DefaultPageSize)
    {
        var loans = await handler
            .HandleAsync(id, new SearchLoansQuery(id, null, status, page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(loans);
    }
}
