using BookRent.Domain.Idempotency;

namespace BookRent.Application.Abstractions.Persistence;

/// <summary>
/// Reserva de <c>Idempotency-Key</c> apoiada no indice unico do PostgreSQL.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Tenta reservar a chave com <c>INSERT ... ON CONFLICT DO NOTHING</c>.
    ///
    /// Devolve <c>true</c> quando esta requisicao ficou dona da chave. Devolve
    /// <c>false</c> quando ela ja pertence a outra — e, se a concorrente ainda nao tiver
    /// commitado, a chamada **bloqueia no indice unico** ate ela terminar. O proprio
    /// PostgreSQL e o mutex: nada de lock distribuido no Redis, que seria uma segunda
    /// fonte de verdade sujeita a expiracao. Ver secao 3 do plano.
    ///
    /// Roda na MESMA transacao do emprestimo: se a operacao falhar, o rollback libera a
    /// chave em vez de queima-la.
    /// </summary>
    Task<bool> TryClaimAsync(IdempotencyRecord record, CancellationToken cancellationToken = default);

    Task<IdempotencyRecord?> FindAsync(string endpoint, string key, CancellationToken cancellationToken = default);

    /// <summary>Guarda a resposta produzida, para que um retry receba exatamente a mesma.</summary>
    Task CompleteAsync(
        string endpoint,
        string key,
        int responseStatus,
        string responseBody,
        Guid? loanId,
        CancellationToken cancellationToken = default);
}
