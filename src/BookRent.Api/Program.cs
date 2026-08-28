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

// Relogio injetado: o dominio recebe o instante como parametro e os testes nao
// precisam de truque para controla-lo.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Correlacao: uma instancia por requisicao, exposta ao dominio pela interface.
builder.Services.AddScoped<LoanMetricsFilter>();
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

        // A correlacao vem do contexto de requisicao, e nao do cabecalho da resposta:
        // o UseExceptionHandler limpa a resposta antes de reexecutar o pipeline, entao
        // nesse ponto o cabecalho ja nao esta mais la — e toda resposta de erro sairia
        // com correlationId vazio, justamente onde ele mais importa.
        var correlationId = context.HttpContext.RequestServices
            .GetRequiredService<ICorrelationContext>()
            .CorrelationId;

        context.ProblemDetails.Extensions["correlationId"] = correlationId;

        // Reposto tambem no cabecalho, para o cliente correlacionar sem ler o corpo.
        context.HttpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
    };
});

// Ordem importa: o primeiro handler que reconhecer a excecao responde.
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddExceptionHandler<PersistenceExceptionHandler>();
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
