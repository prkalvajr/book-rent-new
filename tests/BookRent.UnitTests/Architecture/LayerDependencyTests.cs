using System.Reflection;
using BookRent.Domain.Common;
using Shouldly;

namespace BookRent.UnitTests.Architecture;

/// <summary>
/// Guarda a regra de dependencia da Clean Architecture: o dominio nao pode
/// conhecer aplicacao, infraestrutura ou qualquer framework de persistencia.
/// Este teste falha no momento em que alguem adicionar a referencia errada.
/// </summary>
public class LayerDependencyTests
{
    private static readonly string[] ForbiddenInDomain =
    [
        "BookRent.Application",
        "BookRent.Infrastructure",
        "BookRent.Api",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "StackExchange.Redis",
    ];

    [Fact]
    public void Dominio_nao_deve_referenciar_outras_camadas_nem_infraestrutura()
    {
        var domainAssembly = typeof(Entity).Assembly;

        var referencedAssemblies = domainAssembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => name is not null)
            .ToArray();

        foreach (var forbidden in ForbiddenInDomain)
        {
            referencedAssemblies.ShouldNotContain(
                forbidden,
                $"A camada de dominio nao pode depender de {forbidden}.");
        }
    }

    [Fact]
    public void Aplicacao_nao_deve_referenciar_infraestrutura_nem_api()
    {
        var applicationAssembly = Assembly.Load("BookRent.Application");

        var referencedAssemblies = applicationAssembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        referencedAssemblies.ShouldNotContain("BookRent.Infrastructure");
        referencedAssemblies.ShouldNotContain("BookRent.Api");
    }
}
