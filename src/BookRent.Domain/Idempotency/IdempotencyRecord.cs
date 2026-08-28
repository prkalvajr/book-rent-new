namespace BookRent.Domain.Idempotency;

/// <summary>
/// Reserva de uma <c>Idempotency-Key</c> e a resposta que ela produziu.
///
/// A chave e a propria PK: o indice unico do PostgreSQL e o que garante que duas
/// requisicoes concorrentes com a mesma chave nao criem dois emprestimos. A segunda
/// BLOQUEIA no indice ate a primeira commitar — o banco e o mutex, sem lock distribuido.
///
/// Gravado na MESMA transacao do emprestimo: ou existem os dois, ou nenhum. Por isso
/// uma requisicao que falha faz rollback e LIBERA a chave, em vez de queima-la.
/// </summary>
public sealed class IdempotencyRecord
{
    public const int MaxKeyLength = 200;
    public const int MaxEndpointLength = 100;
    public const int RequestHashLength = 64;

    /// <summary>Construtor exigido pelo materializador do EF Core.</summary>
    private IdempotencyRecord()
    {
    }

    private IdempotencyRecord(
        string key,
        string endpoint,
        string requestHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Key = key;
        Endpoint = endpoint;
        RequestHash = requestHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Valor do cabecalho <c>Idempotency-Key</c>. Chave primaria junto com o endpoint.</summary>
    public string Key { get; private set; } = null!;

    /// <summary>Escopo da chave: dois endpoints podem receber a mesma string sem colidir.</summary>
    public string Endpoint { get; private set; } = null!;

    /// <summary>SHA-256 do corpo canonico, para detectar reuso da chave com payload diferente.</summary>
    public string RequestHash { get; private set; } = null!;

    public int? ResponseStatus { get; private set; }

    /// <summary>Resposta ja produzida, devolvida no replay. Mapeado para jsonb.</summary>
    public string? ResponseBody { get; private set; }

    public Guid? LoanId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Uma reserva sem resposta pertence a uma transacao que ainda nao commitou.</summary>
    public bool IsCompleted => ResponseStatus.HasValue;

    public static IdempotencyRecord Claim(
        string key,
        string endpoint,
        string requestHash,
        DateTimeOffset now,
        TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        return new IdempotencyRecord(key, endpoint, requestHash, now, now + retention);
    }

    // A resposta produzida e gravada por IIdempotencyStore.CompleteAsync, com UPDATE
    // direto: o registro foi inserido por SQL explicito (ON CONFLICT DO NOTHING) e nunca
    // esta no change tracker, entao um metodo de dominio aqui nao teria como persistir.

    /// <summary>Mesma chave com corpo diferente e erro do cliente, nao replay.</summary>
    public bool MatchesRequest(string requestHash) =>
        string.Equals(RequestHash, requestHash, StringComparison.Ordinal);
}
