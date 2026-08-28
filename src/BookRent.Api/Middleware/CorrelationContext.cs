using BookRent.Application.Abstractions.Correlation;

namespace BookRent.Api.Middleware;

/// <summary>
/// Implementacao com escopo de requisicao de <see cref="ICorrelationContext"/>,
/// preenchida por <see cref="CorrelationIdMiddleware"/>.
/// </summary>
internal sealed class CorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; private set; } = string.Empty;

    public string Actor { get; private set; } = "anonymous";

    public void Set(string correlationId, string actor)
    {
        CorrelationId = correlationId;
        Actor = actor;
    }
}
