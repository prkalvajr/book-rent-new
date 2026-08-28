using System.Collections.Frozen;
using BookRent.Domain.Books;
using BookRent.Domain.Loans;
using BookRent.Domain.Users;

namespace BookRent.Api.Middleware;

/// <summary>
/// Traduz o codigo estavel da regra de negocio no status HTTP correspondente.
///
/// O mapa vive na camada de apresentacao de proposito: status HTTP e detalhe de
/// transporte, e o dominio nao deve conhece-lo. A separacao segue a secao 6.1 do plano:
///
///   404 recurso inexistente
///   409 conflito de ESTADO — o pedido faz sentido, mas o estado atual o impede
///   422 semantica invalida do payload — repetir sem mudar nao vai adiantar
///
/// A distincao entre 409 e 422 importa para o cliente decidir se vale a pena repetir.
/// </summary>
internal static class DomainErrorStatusMap
{
    private static readonly FrozenDictionary<string, int> Map = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // Recurso inexistente.
        [BookErrors.NotFound] = StatusCodes.Status404NotFound,
        [UserErrors.NotFound] = StatusCodes.Status404NotFound,
        [LoanErrors.NotFound] = StatusCodes.Status404NotFound,

        // Conflito de estado.
        [BookErrors.IsbnAlreadyExists] = StatusCodes.Status409Conflict,
        [BookErrors.HasActiveLoans] = StatusCodes.Status409Conflict,
        [BookErrors.Inactive] = StatusCodes.Status409Conflict,
        [BookErrors.AlreadyInactive] = StatusCodes.Status409Conflict,
        [BookErrors.ConcurrentModification] = StatusCodes.Status409Conflict,
        [UserErrors.EmailAlreadyExists] = StatusCodes.Status409Conflict,
        [LoanErrors.NoCopiesAvailable] = StatusCodes.Status409Conflict,
        [LoanErrors.NotActive] = StatusCodes.Status409Conflict,

        // Semantica invalida do payload.
        [BookErrors.TitleRequired] = StatusCodes.Status422UnprocessableEntity,
        [BookErrors.AuthorRequired] = StatusCodes.Status422UnprocessableEntity,
        [BookErrors.IsbnInvalid] = StatusCodes.Status422UnprocessableEntity,
        [BookErrors.TotalCopiesNegative] = StatusCodes.Status422UnprocessableEntity,
        [BookErrors.TotalBelowActiveLoans] = StatusCodes.Status422UnprocessableEntity,
        [BookErrors.AvailabilityOverflow] = StatusCodes.Status422UnprocessableEntity,
        [UserErrors.NameRequired] = StatusCodes.Status422UnprocessableEntity,
        [UserErrors.EmailInvalid] = StatusCodes.Status422UnprocessableEntity,
        [LoanErrors.ActorRequired] = StatusCodes.Status422UnprocessableEntity,
        [LoanErrors.InvalidLoanPeriod] = StatusCodes.Status422UnprocessableEntity,
        [LoanErrors.IdempotencyKeyReused] = StatusCodes.Status422UnprocessableEntity,

        // Requisicao malformada.
        [LoanErrors.IdempotencyKeyRequired] = StatusCodes.Status400BadRequest,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Codigo desconhecido cai em 409: um codigo novo e, por definicao, uma regra de
    /// negocio que ainda nao foi classificada, e conflito e a leitura mais conservadora.
    /// </summary>
    public static int ToStatusCode(string domainErrorCode) =>
        Map.TryGetValue(domainErrorCode, out var status) ? status : StatusCodes.Status409Conflict;

    public static string ToTitle(int statusCode) => statusCode switch
    {
        StatusCodes.Status404NotFound => "Recurso nao encontrado",
        StatusCodes.Status400BadRequest => "Requisicao invalida",
        StatusCodes.Status422UnprocessableEntity => "Conteudo nao processavel",
        _ => "Regra de negocio violada",
    };
}
