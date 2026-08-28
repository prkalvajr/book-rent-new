using BookRent.Api.Diagnostics;
using BookRent.Application.Abstractions.Correlation;
using BookRent.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BookRent.Api.Middleware;

/// <summary>
/// Traduz <see cref="DomainException"/> em uma resposta 409 com Problem Details,
/// preservando o codigo da regra violada. Demais excecoes caem no handler padrao (500).
/// </summary>
internal sealed class DomainExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not DomainException domainException)
        {
            return false;
        }

        ApiLog.DomainRuleViolated(logger, domainException.Code, domainException);

        // IExceptionHandler e singleton; o contexto de correlacao tem escopo de
        // requisicao e por isso e resolvido a partir do proprio HttpContext.
        var correlationId = httpContext.RequestServices
            .GetRequiredService<ICorrelationContext>()
            .CorrelationId;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = domainException,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Regra de negocio violada",
                Detail = domainException.Message,
                Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.10",
                Extensions =
                {
                    ["code"] = domainException.Code,
                    ["correlationId"] = correlationId,
                },
            },
        }).ConfigureAwait(false);
    }
}
