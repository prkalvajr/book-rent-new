using BookRent.Application.Auditing;
using BookRent.Application.Books;
using BookRent.Application.Loans;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace BookRent.Api.Endpoints;

/// <summary>Endpoints de emprestimo, disponibilidade, historico e trilha de auditoria.</summary>
internal static class LoanEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Sinaliza ao cliente que a resposta veio de uma chamada anterior.</summary>
    public const string ReplayedHeader = "Idempotency-Replayed";

    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var loans = endpoints.MapGroup("/loans").WithTags("Emprestimos");

        loans.MapPost("/", CreateAsync)
            .AddEndpointFilter<LoanMetricsFilter>()
            .WithSummary("Cria um emprestimo (exige Idempotency-Key)")
            .Produces<LoanResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        loans.MapGet("/{id:guid}", GetAsync)
            .WithSummary("Consulta um emprestimo, em qualquer estado")
            .Produces<LoanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        loans.MapPost("/{id:guid}/return", ReturnAsync)
            .WithSummary("Devolve um emprestimo; o registro permanece no historico")
            .Produces<LoanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        loans.MapPost("/{id:guid}/cancel", CancelAsync)
            .WithSummary("Cancela um emprestimo, preservando que ele existiu")
            .Produces<LoanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var books = endpoints.MapGroup("/books").WithTags("Livros");

        books.MapGet("/{id:guid}/availability", GetAvailabilityAsync)
            .WithSummary("Disponibilidade atual do livro (leitura cacheada)")
            .Produces<BookAvailabilityResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        books.MapGet("/{id:guid}/history", GetHistoryAsync)
            .WithSummary("Historico de emprestimos do livro, em qualquer estado")
            .Produces<PagedResult<LoanResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/audit-events", SearchAuditEventsAsync)
            .WithTags("Auditoria")
            .WithSummary("Consulta a trilha de auditoria de negocio")
            .Produces<PagedResult<AuditEventResponse>>();

        return endpoints;
    }

    /// <summary>
    /// Replay devolve o MESMO 201 da chamada original — o desafio pede "a resposta
    /// previamente produzida" —, distinguido pelo cabecalho Idempotency-Replayed.
    /// Devolver 409 aqui contradiria o proposito da idempotencia.
    /// </summary>
    private static async Task<Created<LoanResponse>> CreateAsync(
        CreateLoanRequest request,
        CreateLoanHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var key = httpContext.Request.Headers[IdempotencyKeyHeader].FirstOrDefault();

        var result = await handler.HandleAsync(key, request, cancellationToken).ConfigureAwait(false);

        httpContext.Response.Headers[ReplayedHeader] = result.Replayed ? "true" : "false";

        return TypedResults.Created($"/loans/{result.Loan.Id}", result.Loan);
    }

    private static async Task<Ok<LoanResponse>> GetAsync(
        Guid id,
        GetLoanHandler handler,
        CancellationToken cancellationToken)
    {
        var loan = await handler.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(loan);
    }

    private static async Task<Ok<LoanResponse>> ReturnAsync(
        Guid id,
        LoanLifecycleHandler handler,
        CancellationToken cancellationToken)
    {
        var loan = await handler.ReturnAsync(id, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(loan);
    }

    private static async Task<Ok<LoanResponse>> CancelAsync(
        Guid id,
        LoanLifecycleHandler handler,
        CancellationToken cancellationToken)
    {
        var loan = await handler.CancelAsync(id, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(loan);
    }

    private static async Task<Ok<BookAvailabilityResponse>> GetAvailabilityAsync(
        Guid id,
        GetBookAvailabilityHandler handler,
        CancellationToken cancellationToken)
    {
        var availability = await handler.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(availability);
    }

    private static async Task<Ok<PagedResult<LoanResponse>>> GetHistoryAsync(
        Guid id,
        GetBookHistoryHandler handler,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = SearchLoansQuery.DefaultPageSize)
    {
        var history = await handler
            .HandleAsync(id, new SearchLoansQuery(null, id, null, page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(history);
    }

    private static async Task<Ok<PagedResult<AuditEventResponse>>> SearchAuditEventsAsync(
        SearchAuditEventsHandler handler,
        CancellationToken cancellationToken,
        [FromQuery] string? entityType = null,
        [FromQuery] Guid? entityId = null,
        [FromQuery] string? action = null,
        [FromQuery] string? actor = null,
        [FromQuery] string? correlationId = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = SearchAuditEventsQuery.DefaultPageSize)
    {
        var events = await handler
            .HandleAsync(
                new SearchAuditEventsQuery(entityType, entityId, action, actor, correlationId, from, to, page, pageSize),
                cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(events);
    }
}
