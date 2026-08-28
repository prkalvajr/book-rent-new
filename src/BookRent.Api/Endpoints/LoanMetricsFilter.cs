using System.Diagnostics;
using BookRent.Api.Diagnostics;
using BookRent.Domain.Common;

namespace BookRent.Api.Endpoints;

/// <summary>
/// Instrumenta o endpoint de criacao de emprestimo com as metricas de negocio exigidas
/// pelo desafio: criados, rejeicoes por regra, repeticoes idempotentes e latencia.
///
/// Fica na camada de apresentacao de proposito. A duracao medida e a do ENDPOINT, e as
/// rejeicoes chegam aqui como <see cref="DomainException"/> antes de virarem Problem
/// Details — um filtro de endpoint envolve o handler, entao enxerga sucesso e falha sem
/// que a camada de aplicacao precise conhecer telemetria de HTTP.
/// </summary>
internal sealed class LoanMetricsFilter(LoanMetrics metrics) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var started = Stopwatch.GetTimestamp();
        var outcome = "error";

        try
        {
            var result = await next(context).ConfigureAwait(false);

            var replayed = string.Equals(
                context.HttpContext.Response.Headers[LoanEndpoints.ReplayedHeader],
                "true",
                StringComparison.Ordinal);

            if (replayed)
            {
                // Reaproveitou resposta ja produzida: nenhum emprestimo novo, nenhuma
                // disponibilidade decrementada de novo.
                metrics.IdempotentReplay();
                outcome = "replayed";
            }
            else
            {
                metrics.LoanCreated();
                outcome = "created";
            }

            return result;
        }
        catch (DomainException exception)
        {
            // O codigo da regra e a tag: permite separar "sem exemplar" de "chave
            // reusada" no painel, que e a pergunta que se faz na madrugada.
            metrics.LoanRejected(exception.Code);
            outcome = "rejected";

            throw;
        }
        finally
        {
            metrics.RecordLoanRequestDuration(Stopwatch.GetElapsedTime(started).TotalSeconds, outcome);
        }
    }
}
