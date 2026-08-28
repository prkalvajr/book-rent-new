namespace BookRent.Domain.Common;

/// <summary>
/// Violacao de invariante ou de regra de negocio do dominio.
/// A camada de API traduz esta excecao em uma resposta 409/422 com Problem Details.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message) => Code = code;

    public DomainException(string code, string message, Exception innerException)
        : base(message, innerException) => Code = code;

    /// <summary>Codigo estavel da regra violada (ex.: "loan.no_copies_available").</summary>
    public string Code { get; } = string.Empty;
}
