namespace BookRent.Api.Endpoints;

/// <summary>
/// Ponto unico de registro dos endpoints de negocio.
/// Cada grupo (books, users, loans, audit-events) e adicionado aqui conforme
/// e implementado, mantendo o <c>Program.cs</c> enxuto.
/// </summary>
internal static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapBookRentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapBookEndpoints();

        endpoints.MapUserEndpoints();
        // Emprestimos, disponibilidade, historico e trilha de auditoria.
        endpoints.MapLoanEndpoints();

        return endpoints;
    }
}
