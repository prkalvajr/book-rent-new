using System.Diagnostics;
using Serilog.Context;

namespace BookRent.Api.Middleware;

/// <summary>
/// Resolve o <c>correlationId</c> da requisicao (cabecalho recebido ou gerado),
/// devolve-o na resposta, anexa-o aos logs estruturados e ao span corrente.
/// E o mesmo identificador gravado nos eventos de auditoria.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ActorHeaderName = "X-Actor-Id";

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlationContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(correlationContext);

        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            // Cai para o trace id quando ha um span ativo, mantendo log e trace alinhados.
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("n");
        }

        var actor = context.Request.Headers[ActorHeaderName].FirstOrDefault() ?? "anonymous";

        correlationContext.Set(correlationId, actor);
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("bookrent.correlation_id", correlationId);

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("Actor", actor))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}
