using BookRent.Api.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace BookRent.Api.Extensions;

/// <summary>Logs estruturados (Serilog) e traces/metricas (OpenTelemetry).</summary>
internal static class ObservabilityExtensions
{
    /// <summary>ActivitySource emitida pelo proprio driver Npgsql para comandos SQL.</summary>
    private const string NpgsqlActivitySourceName = "Npgsql";

    public static IHostApplicationBuilder AddStructuredLogging(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("service.name", BookRentDiagnostics.ServiceName));

        return builder;
    }

    public static IHostApplicationBuilder AddOpenTelemetry(this IHostApplicationBuilder builder)
    {
        // Vazio => sem exportador: a aplicacao roda normalmente fora do Compose,
        // apenas sem enviar telemetria para lugar nenhum.
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var hasOtlpExporter = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddSingleton<LoanMetrics>();

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: BookRentDiagnostics.ServiceName,
                    serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    serviceInstanceId: Environment.MachineName)
                .AddEnvironmentVariableDetector())
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(BookRentDiagnostics.ActivitySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(NpgsqlActivitySourceName);

                if (hasOtlpExporter)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(BookRentDiagnostics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (hasOtlpExporter)
                {
                    metrics.AddOtlpExporter();
                }
            });

        return builder;
    }

    /// <summary>Log de uma linha por requisicao, com correlationId e dados da rota.</summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        return app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} respondeu {StatusCode} em {Elapsed:0.0000} ms";

            options.GetLevel = static (httpContext, elapsed, exception) =>
                exception is not null || httpContext.Response.StatusCode >= 500
                    ? LogEventLevel.Error
                    : httpContext.Request.Path.StartsWithSegments("/health")
                        ? LogEventLevel.Verbose
                        : LogEventLevel.Information;

            options.EnrichDiagnosticContext = static (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
            };
        });
    }
}
