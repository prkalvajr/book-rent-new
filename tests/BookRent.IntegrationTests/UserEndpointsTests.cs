using System.Net;
using System.Net.Http.Json;
using BookRent.Application.Books;
using BookRent.Application.Loans;
using BookRent.Application.Users;
using BookRent.Domain.Users;
using BookRent.IntegrationTests.Fixtures;
using Shouldly;

namespace BookRent.IntegrationTests;

[Collection(IntegrationTestSuite.Name)]
public class UserEndpointsTests(BookRentApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RegisterUserRequest UmLeitor(string? email = null) =>
        new("Maria Silva", email ?? $"maria.{Guid.CreateVersion7():N}@exemplo.com");

    [Fact]
    public async Task Deve_cadastrar_um_leitor_e_devolver_o_location()
    {
        using var client = factory.CreateClient();

        var request = new RegisterUserRequest("  Maria Silva  ", "  Maria.Silva.Unica@Exemplo.COM ");

        using var response = await client.PostAsJsonAsync(new Uri("/users", UriKind.Relative), request, Ct);

        var corpo = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, corpo);
        response.Headers.Location!.ToString().ShouldContain("/users/");

        var leitor = await response.Content.ReadFromJsonAsync<UserResponse>(Ct);

        leitor!.Name.ShouldBe("Maria Silva");
        leitor.Email.ShouldBe("maria.silva.unica@exemplo.com", "o e-mail e normalizado antes de persistir");
    }

    [Fact]
    public async Task O_location_devolvido_no_cadastro_deve_responder()
    {
        using var client = factory.CreateClient();

        using var criado = await client.PostAsJsonAsync(new Uri("/users", UriKind.Relative), UmLeitor(), Ct);
        criado.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var seguido = await client.GetAsync(criado.Headers.Location, Ct);

        seguido.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deve_recusar_email_duplicado_com_409()
    {
        using var client = factory.CreateClient();
        var request = UmLeitor();

        using var primeiro = await client.PostAsJsonAsync(new Uri("/users", UriKind.Relative), request, Ct);
        primeiro.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Mesmo e-mail em outra caixa: a normalizacao faz colidir, que e o esperado.
        var duplicado = request with { Email = request.Email!.ToUpperInvariant() };

        using var segundo = await client.PostAsJsonAsync(new Uri("/users", UriKind.Relative), duplicado, Ct);

        segundo.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await segundo.CodigoDoProblemaAsync(Ct)).ShouldBe(UserErrors.EmailAlreadyExists);
    }

    [Theory]
    [InlineData("", "maria@exemplo.com", UserErrors.NameRequired)]
    [InlineData("Maria", "sem-arroba", UserErrors.EmailInvalid)]
    [InlineData("Maria", "", UserErrors.EmailInvalid)]
    public async Task Deve_recusar_payload_invalido_com_422(string nome, string email, string codigoEsperado)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/users", UriKind.Relative),
            new RegisterUserRequest(nome, email),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.CodigoDoProblemaAsync(Ct)).ShouldBe(codigoEsperado);
    }

    [Fact]
    public async Task Leitor_inexistente_deve_responder_404()
    {
        using var client = factory.CreateClient();
        var inexistente = Guid.CreateVersion7();

        using var porId = await client.GetAsync(new Uri($"/users/{inexistente}", UriKind.Relative), Ct);
        porId.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await porId.CodigoDoProblemaAsync(Ct)).ShouldBe(UserErrors.NotFound);

        // Tambem no historico: 404 aqui e "leitor nao existe", nao "nao tem emprestimo".
        using var historico = await client.GetAsync(new Uri($"/users/{inexistente}/loans", UriKind.Relative), Ct);
        historico.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await historico.CodigoDoProblemaAsync(Ct)).ShouldBe(UserErrors.NotFound);
    }

    [Fact]
    public async Task Leitor_sem_emprestimos_deve_devolver_pagina_vazia_e_nao_404()
    {
        using var client = factory.CreateClient();

        using var criado = await client.PostAsJsonAsync(new Uri("/users", UriKind.Relative), UmLeitor(), Ct);
        var leitor = await criado.Content.ReadFromJsonAsync<UserResponse>(Ct);

        var historico = await client.GetFromJsonAsync<PagedResult<LoanResponse>>(
            new Uri($"/users/{leitor!.Id}/loans", UriKind.Relative), Ct);

        historico!.Items.ShouldBeEmpty();
        historico.TotalCount.ShouldBe(0);
        historico.Page.ShouldBe(1);
    }
}
