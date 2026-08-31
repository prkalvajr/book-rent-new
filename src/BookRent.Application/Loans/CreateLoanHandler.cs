using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookRent.Application.Abstractions.Auditing;
using BookRent.Application.Abstractions.Caching;
using BookRent.Application.Abstractions.Correlation;
using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Auditing;
using BookRent.Domain.Books;
using BookRent.Domain.Common;
using BookRent.Domain.Idempotency;
using BookRent.Domain.Loans;
using BookRent.Domain.Users;
using Microsoft.Extensions.Options;

namespace BookRent.Application.Loans;

/// <summary>
/// Criacao de emprestimo: o caminho critico do desafio.
///
/// Tudo acontece numa transacao so — reserva da chave de idempotencia, decremento da
/// disponibilidade, emprestimo, auditoria e a resposta gravada. Ou todos existem, ou
/// nenhum. Duas consequencias que valem enunciar:
///
///   1. Uma requisicao que FALHA faz rollback e libera a Idempotency-Key, em vez de
///      queima-la por uma tentativa que nao produziu efeito nenhum.
///   2. Um retry apos failover reencontra a chave (se a original commitou) ou nao
///      encontra nada (se deu rollback). Nao existe meia operacao.
/// </summary>
public sealed class CreateLoanHandler(
    IBookRepository books,
    IUserRepository users,
    ILoanRepository loans,
    IIdempotencyStore idempotency,
    IUnitOfWork unitOfWork,
    IAuditTrail auditTrail,
    ICacheStore cache,
    ICorrelationContext correlation,
    TimeProvider timeProvider,
    IOptions<LoanOptions> options)
{
    public const string Endpoint = "POST /loans";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly LoanOptions _options = options.Value;

    public async Task<CreateLoanResult> HandleAsync(
        string? idempotencyKey,
        CreateLoanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = idempotencyKey?.Trim();

        if (string.IsNullOrEmpty(key))
        {
            throw new DomainException(
                LoanErrors.IdempotencyKeyRequired,
                "O cabecalho Idempotency-Key e obrigatorio em POST /loans.");
        }

        if (key.Length > IdempotencyRecord.MaxKeyLength)
        {
            throw new DomainException(
                LoanErrors.IdempotencyKeyRequired,
                $"A Idempotency-Key excede {IdempotencyRecord.MaxKeyLength} caracteres.");
        }

        var now = timeProvider.GetUtcNow();
        var requestHash = HashOf(request);

        var result = await unitOfWork.ExecuteInTransactionAsync(
            ct => CreateOrReplayAsync(key, request, requestHash, now, ct),
            cancellationToken).ConfigureAwait(false);

        // Depois do commit, nunca antes: dentro da transacao, um Redis lento seguraria o
        // row lock do livro e enfileiraria todos os emprestimos daquele titulo. A
        // invalidacao tem teto de tempo proprio e nunca propaga excecao — ver ICacheStore.
        if (!result.Replayed)
        {
            await cache.InvalidateAsync(CacheKeys.Book(request.BookId)).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<CreateLoanResult> CreateOrReplayAsync(
        string key,
        CreateLoanRequest request,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var claim = IdempotencyRecord.Claim(key, Endpoint, requestHash, now, _options.IdempotencyRetention);

        // Se outra requisicao com a mesma chave ainda nao commitou, esta chamada bloqueia
        // no indice unico ate ela terminar.
        if (!await idempotency.TryClaimAsync(claim, cancellationToken).ConfigureAwait(false))
        {
            return await ReplayAsync(key, requestHash, cancellationToken).ConfigureAwait(false);
        }

        await EnsureUserExistsAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        // Coracao do desafio: um unico comando decide e escreve. Zero linhas afetadas ja
        // e a resposta de negocio, nao um erro tecnico a interpretar.
        var reserved = await books.TryReserveCopyAsync(request.BookId, cancellationToken).ConfigureAwait(false);

        if (reserved == 0)
        {
            await ThrowForFailedReservationAsync(request.BookId, cancellationToken).ConfigureAwait(false);
        }

        var loan = Loan.Create(request.BookId, request.UserId, correlation.Actor, now, _options.LoanPeriod);

        loans.Add(loan);

        auditTrail.Record(
            AuditEntityTypes.Loan,
            loan.Id,
            AuditActions.LoanCreated,
            new
            {
                loan.BookId,
                loan.UserId,
                loan.LoanedAt,
                loan.DueAt,
                IdempotencyKey = key,
            });

        var response = loan.ToResponse();

        await idempotency.CompleteAsync(
            Endpoint,
            key,
            StatusCreated,
            JsonSerializer.Serialize(response, SerializerOptions),
            loan.Id,
            cancellationToken).ConfigureAwait(false);

        return new CreateLoanResult(response, Replayed: false);
    }

    /// <summary>
    /// A chave ja pertence a outra requisicao. Corpo igual devolve a resposta produzida
    /// por ela; corpo diferente e erro do cliente, nao replay.
    /// </summary>
    private async Task<CreateLoanResult> ReplayAsync(
        string key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var existing = await idempotency.FindAsync(Endpoint, key, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(
                LoanErrors.IdempotencyKeyReused,
                "A Idempotency-Key esta em uso por outra requisicao que nao concluiu.");

        if (!existing.MatchesRequest(requestHash))
        {
            throw new DomainException(
                LoanErrors.IdempotencyKeyReused,
                "A mesma Idempotency-Key foi usada com um corpo diferente.");
        }

        if (!existing.IsCompleted || existing.ResponseBody is null)
        {
            throw new DomainException(
                LoanErrors.IdempotencyKeyReused,
                "A Idempotency-Key esta em uso por uma requisicao ainda em andamento.");
        }

        var replayed = JsonSerializer.Deserialize<LoanResponse>(existing.ResponseBody, SerializerOptions)
            ?? throw new DomainException(
                LoanErrors.IdempotencyKeyReused,
                "A resposta armazenada para esta Idempotency-Key nao pode ser lida.");

        return new CreateLoanResult(replayed, Replayed: true);
    }

    private async Task EnsureUserExistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await users.ExistsAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(UserErrors.NotFound, $"Leitor {userId} nao encontrado.");
        }
    }

    /// <summary>
    /// O comando condicional nao diz QUAL das tres condicoes falhou. A releitura separa
    /// livro inexistente (404), desativado (409) e sem exemplar (409) — uma consulta a
    /// mais, so no caminho de erro.
    /// </summary>
    private async Task ThrowForFailedReservationAsync(Guid bookId, CancellationToken cancellationToken)
    {
        var book = await books.FindReadOnlyAsync(bookId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(BookErrors.NotFound, $"Livro {bookId} nao encontrado.");

        if (!book.IsActive)
        {
            throw new DomainException(BookErrors.Inactive, "O livro esta desativado e nao pode ser emprestado.");
        }

        throw new DomainException(
            LoanErrors.NoCopiesAvailable,
            "Nao ha exemplares disponiveis deste livro no momento.");
    }

    /// <summary>
    /// Hash do corpo canonico. Serve para distinguir replay legitimo (mesma chave, mesmo
    /// corpo) de reuso indevido da chave (mesma chave, corpo diferente).
    /// </summary>
    private static string HashOf(CreateLoanRequest request)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{request.BookId:D}|{request.UserId:D}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private const int StatusCreated = 201;
}
