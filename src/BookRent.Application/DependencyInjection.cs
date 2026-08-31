using BookRent.Application.Auditing;
using BookRent.Application.Books;
using BookRent.Application.Loans;
using BookRent.Application.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BookRent.Application;

/// <summary>
/// Registro dos casos de uso.
///
/// Sem MediatR: para o numero de casos de uso deste servico, um despachante adicionaria
/// indirecao sem resolver problema nenhum — o rastro de execucao fica direto no codigo.
/// Ver secao 9.4 do plano.
///
/// Sem FluentValidation: a validacao e invariante de negocio e vive no dominio, que a
/// aplica em qualquer caminho de entrada e a expressa com codigos de erro estaveis.
/// Um segundo conjunto de regras na borda duplicaria isso e poderia divergir.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ValidateOnStart sem Validate nao valida NADA. Sem estas regras,
        // Loans__DefaultLoanPeriodDays=0 subia normalmente e fazia todo POST /loans
        // responder 422 em runtime, em vez de falhar no boot.
        services.AddOptions<LoanOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(LoanOptions.SectionName).Bind(options))
            .Validate(
                options => options.DefaultLoanPeriodDays > 0,
                "Loans:DefaultLoanPeriodDays deve ser maior que zero.")
            .Validate(
                options => options.IdempotencyRetention > TimeSpan.Zero,
                "Loans:IdempotencyRetention deve ser positivo. Formato TimeSpan: use d.hh:mm:ss.")
            .ValidateOnStart();

        services.AddScoped<CreateBookHandler>();
        services.AddScoped<GetBookHandler>();
        services.AddScoped<SearchBooksHandler>();
        services.AddScoped<UpdateBookHandler>();
        services.AddScoped<DeactivateBookHandler>();

        services.AddScoped<RegisterUserHandler>();
        services.AddScoped<GetUserHandler>();
        services.AddScoped<GetUserLoansHandler>();

        services.AddScoped<CreateLoanHandler>();
        services.AddScoped<GetLoanHandler>();
        services.AddScoped<LoanLifecycleHandler>();
        services.AddScoped<GetBookAvailabilityHandler>();
        services.AddScoped<GetBookHistoryHandler>();
        services.AddScoped<SearchAuditEventsHandler>();

        return services;
    }
}
