using System.Net.Http.Headers;
using System.Net.Http.Json;
using BookRent.Application.Books;
using BookRent.Application.Loans;
using BookRent.Application.Users;
using Shouldly;

namespace BookRent.IntegrationTests.Fixtures;

/// <summary>
/// Auxiliares dos testes de integracao.
///
/// Os containers sao compartilhados por toda a colecao (ver <see cref="IntegrationTestSuite"/>),
/// entao NENHUM teste pode assumir banco vazio: cada um cria os proprios dados com
/// identificadores unicos e afirma apenas sobre eles.
/// </summary>
internal static class ApiClientExtensions
{
    internal static int _sequence;

    /// <summary>Gera um ISBN-13 valido e unico dentro da execucao da suite.</summary>
    public static string NovoIsbn()
    {
        var sequencial = Interlocked.Increment(ref _sequence);
        var aleatorio = Random.Shared.Next(100_000, 999_999);

        return $"978{aleatorio}{sequencial % 10_000:D4}";
    }

    public static HttpClient ComAtor(this HttpClient client, string actor)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.DefaultRequestHeaders.Remove("X-Actor-Id");
        client.DefaultRequestHeaders.Add("X-Actor-Id", actor);

        return client;
    }

    public static async Task<BookResponse> CriarLivroAsync(
        this HttpClient client,
        int exemplares = 3,
        string? titulo = null,
        string? autor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new CreateBookRequest(
            titulo ?? $"Livro {Guid.CreateVersion7()}",
            NovoIsbn(),
            autor ?? "Autor de Teste",
            exemplares);

        using var response = await client.PostAsJsonAsync(
            new Uri("/books", UriKind.Relative),
            request,
            cancellationToken);

        var corpo = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, corpo);

        return (await response.Content.ReadFromJsonAsync<BookResponse>(cancellationToken))!;
    }

    /// <summary>Le a extension "code" do Problem Details — o contrato estavel do erro.</summary>
    public static async Task<string?> CodigoDoProblemaAsync(
        this HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var problema = await response.Content.ReadFromJsonAsync<ProblemaComCodigo>(cancellationToken);

        return problema?.Code;
    }

    public static async Task<UserResponse> CriarLeitorAsync(
        this HttpClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new RegisterUserRequest("Leitor de Teste", $"leitor.{Guid.CreateVersion7():N}@exemplo.com");

        using var response = await client.PostAsJsonAsync(
            new Uri("/users", UriKind.Relative),
            request,
            cancellationToken);

        var corpo = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, corpo);

        return (await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken))!;
    }

    /// <summary>Dispara POST /loans sem afirmar nada — quem chama inspeciona a resposta.</summary>
    public static Task<HttpResponseMessage> TentarEmprestarAsync(
        this HttpClient client,
        Guid bookId,
        Guid userId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/loans", UriKind.Relative))
        {
            Content = JsonContent.Create(new CreateLoanRequest(bookId, userId)),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(request, cancellationToken);
    }

    public static async Task<LoanResponse> EmprestarAsync(
        this HttpClient client,
        Guid bookId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.TentarEmprestarAsync(
            bookId, userId, Guid.CreateVersion7().ToString(), cancellationToken);

        var corpo = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, corpo);

        return (await response.Content.ReadFromJsonAsync<LoanResponse>(cancellationToken))!;
    }

    private sealed record ProblemaComCodigo(string? Code, string? CorrelationId, string? Detail);
}
