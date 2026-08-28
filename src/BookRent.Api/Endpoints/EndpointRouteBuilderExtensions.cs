namespace BookRent.Api.Endpoints;

/// <summary>
/// Ponto unico de registro dos endpoints de negocio.
/// Cada grupo (books, users, loans, audit-events) sera adicionado aqui conforme
/// for implementado, mantendo o <c>Program.cs</c> enxuto.
/// </summary>
internal static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapBookRentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var api = endpoints.MapGroup(string.Empty)
            .WithTags("BookRent");

        // api.MapBookEndpoints();
        // api.MapUserEndpoints();
        // api.MapLoanEndpoints();
        // api.MapAuditEventEndpoints();

        _ = api;

        return endpoints;
    }
}
