using System.Text.Json;
using BookRent.Infrastructure.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BookRent.Api.Extensions;

/// <summary>
/// Mapeia os dois endpoints de health exigidos pelo desafio.
/// <c>/health/live</c> nao pode falhar por causa de PostgreSQL/Redis fora do ar —
/// caso contrario o Kubernetes reiniciaria pods saudaveis durante uma queda de dependencia.
/// </summary>
internal static class HealthCheckEndpointExtensions
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthCheckTags.Live),
            ResponseWriter = WriteResponseAsync,
        }).AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthCheckTags.Ready),
            ResponseWriter = WriteResponseAsync,
        }).AllowAnonymous();

        return endpoints;
    }

    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,
                description = entry.Value.Description,
                error = entry.Value.Exception?.Message,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions), context.RequestAborted);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
