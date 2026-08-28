using BookRent.Api.Diagnostics;
using BookRent.Application.Abstractions.Correlation;
using BookRent.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BookRent.Api.Middleware;

/// <summary>
/// Traduz <see cref="DomainException"/> em Problem Details, com o status derivado do
/// codigo da regra violada (ver <see cref="DomainErrorStatusMap"/>) e o codigo preservado
/// na extension <c>code</c> — que e o contrato estavel com o cliente, nao a mensagem.
/// Demais excecoes caem no handler padrao (500).
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

        var statusCode = DomainErrorStatusMap.ToStatusCode(domainException.Code);

        httpContext.Response.StatusCode = statusCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = domainException,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = DomainErrorStatusMap.ToTitle(statusCode),
                Detail = domainException.Message,
                Type = $"https://httpstatuses.io/{statusCode}",
                Extensions =
                {
                    ["code"] = domainException.Code,
                    ["correlationId"] = correlationId,
                },
            },
        }).ConfigureAwait(false);
    }
}
