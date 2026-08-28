namespace BookRent.Application.Abstractions.Correlation;

/// <summary>
/// Identificadores da requisicao corrente, propagados para logs estruturados,
/// traces e eventos de auditoria.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>Valor do cabecalho <c>X-Correlation-Id</c>, ou um gerado quando ausente.</summary>
    string CorrelationId { get; }

    /// <summary>Ator responsavel pela operacao, registrado na trilha de auditoria.</summary>
    string Actor { get; }
}
