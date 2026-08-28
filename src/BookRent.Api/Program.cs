using BookRent.Api.Diagnostics;
using BookRent.Api.Endpoints;
using BookRent.Api.Extensions;
using BookRent.Api.Middleware;
using BookRent.Application;
using BookRent.Application.Abstractions.Correlation;
using BookRent.Infrastructure;
using BookRent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddStructuredLogging();
builder.AddOpenTelemetry();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Correlacao: uma instancia por requisicao, exposta ao dominio pela interface.
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.ProblemDetails.Extensions["correlationId"] =
            context.HttpContext.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
    };
});

builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthEndpoints();
app.MapBookRentEndpoints();

// Aplica migrations no startup somente quando "Database:MigrateOnStartup" estiver ligado.
// O padrao e desligado: com varias replicas a migration deve rodar em um Job dedicado
// (ver deploy/k8s/migration-job.yaml) antes do rollout, nunca em N pods ao mesmo tempo.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<BookRentDbContext>();

    ApiLog.ApplyingMigrations(app.Logger);
    await dbContext.Database.MigrateAsync().ConfigureAwait(false);
}

ApiLog.Starting(app.Logger, BookRentDiagnostics.ServiceName, app.Environment.EnvironmentName);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Expoe a classe gerada do <c>Program</c> para a WebApplicationFactory dos testes.</summary>
public partial class Program;
