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

        services.AddOptions<LoanOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(LoanOptions.SectionName).Bind(options))
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
        services.AddScoped<LoanLifecycleHandler>();
        services.AddScoped<GetBookAvailabilityHandler>();
        services.AddScoped<GetBookHistoryHandler>();
        services.AddScoped<SearchAuditEventsHandler>();

        return services;
    }
}
