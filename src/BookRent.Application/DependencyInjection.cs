using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace BookRent.Application;

/// <summary>Registro dos servicos da camada de aplicacao.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly(), includeInternalTypes: true);

        // Casos de uso serao registrados aqui conforme forem implementados.
        return services;
    }
}
