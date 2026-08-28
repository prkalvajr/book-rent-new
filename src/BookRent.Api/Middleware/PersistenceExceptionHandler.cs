using BookRent.Api.Diagnostics;
using BookRent.Application.Abstractions.Correlation;
using BookRent.Domain.Books;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BookRent.Api.Middleware;

/// <summary>
/// Traduz falhas de persistencia que sao, na verdade, conflitos de negocio.
///
/// <see cref="DbUpdateConcurrencyException"/>: o EF Core a lanca quando um UPDATE com
/// token de concorrencia afeta zero linhas — alguem alterou o registro entre a leitura
/// e a gravacao. Vira 409, sem retry automatico: repetir sozinho significaria mesclar
/// duas edicoes humanas por conta propria, e mesclagem automatica perde intencao.
/// O cliente recebe o conflito e decide. Ver secao 6.5 do plano.
///
/// Violacao de indice unico (SQLSTATE 23505): a checagem previa de ISBN e amigavel, mas
/// nao e a garantia — duas criacoes simultaneas com o mesmo ISBN passam pela checagem e
/// colidem no indice. E o indice que garante, e o erro dele e um 409 de negocio.
/// </summary>
internal sealed class PersistenceExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<PersistenceExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var (code, detail) = Classify(exception);

        if (code is null)
        {
            return false;
        }

        ApiLog.DomainRuleViolated(logger, code, exception!);

        var correlationId = httpContext.RequestServices
            .GetRequiredService<ICorrelationContext>()
            .CorrelationId;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Regra de negocio violada",
                Detail = detail,
                Type = "https://httpstatuses.io/409",
                Extensions =
                {
                    ["code"] = code,
                    ["correlationId"] = correlationId,
                },
            },
        }).ConfigureAwait(false);
    }

    private static (string? Code, string? Detail) Classify(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => (
            BookErrors.ConcurrentModification,
            "O registro foi alterado por outra operacao. Recarregue e tente novamente."),

        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation } =>
            (ResolveUniqueViolation(violation.ConstraintName), "Ja existe um registro com esse valor unico."),

        _ => (null, null),
    };

    private static string ResolveUniqueViolation(string? constraintName) => constraintName switch
    {
        "ux_books_isbn" => BookErrors.IsbnAlreadyExists,
        "ux_users_email" => "user.email_already_exists",
        _ => "persistence.unique_violation",
    };
}
