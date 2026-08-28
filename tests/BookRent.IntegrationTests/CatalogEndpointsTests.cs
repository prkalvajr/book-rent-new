using System.Net;
using System.Net.Http.Json;
using BookRent.Application.Abstractions.Caching;
using BookRent.Application.Books;
using BookRent.Domain.Auditing;
using BookRent.Domain.Books;
using BookRent.Infrastructure.Persistence;
using BookRent.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using StackExchange.Redis;

namespace BookRent.IntegrationTests;

/// <summary>
/// Catalogo ponta a ponta, contra PostgreSQL e Redis reais: pipeline HTTP completa,
/// Problem Details, cache e trilha de auditoria.
/// </summary>
[Collection(IntegrationTestSuite.Name)]
public class CatalogEndpointsTests(BookRentApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deve_criar_um_livro_e_devolver_o_location()
    {
        using var client = factory.CreateClient().ComAtor("bibliotecaria");

        var request = new CreateBookRequest("Dom Casmurro", "978-85-359-1066-3", "Machado de Assis", 4);

        using var response = await client.PostAsJsonAsync(new Uri("/books", UriKind.Relative), request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldContain("/books/");

        var livro = await response.Content.ReadFromJsonAsync<BookResponse>(Ct);

        livro!.Title.ShouldBe("Dom Casmurro");
        livro.Isbn.ShouldBe("9788535910663", "o ISBN e normalizado antes de persistir");
        livro.TotalCopies.ShouldBe(4);
        livro.AvailableCopies.ShouldBe(4);
        livro.IsActive.ShouldBeTrue();
        livro.Version.ShouldBe(1);
    }

    [Fact]
    public async Task Deve_recusar_isbn_duplicado_com_409()
    {
        using var client = factory.CreateClient();
        var existente = await client.CriarLivroAsync(cancellationToken: Ct);

        var duplicado = new CreateBookRequest("Outro titulo", existente.Isbn, "Outro autor", 1);

        using var response = await client.PostAsJsonAsync(new Uri("/books", UriKind.Relative), duplicado, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.CodigoDoProblemaAsync(Ct)).ShouldBe(BookErrors.IsbnAlreadyExists);
    }

    [Theory]
    [InlineData("", "9788535910663", "Autor", 1, BookErrors.TitleRequired)]
    [InlineData("Titulo", "123", "Autor", 1, BookErrors.IsbnInvalid)]
    [InlineData("Titulo", "9788535910663", "", 1, BookErrors.AuthorRequired)]
    [InlineData("Titulo", "9788535910663", "Autor", -1, BookErrors.TotalCopiesNegative)]
    public async Task Deve_recusar_payload_invalido_com_422(
        string titulo,
        string isbn,
        string autor,
        int exemplares,
        string codigoEsperado)
    {
        using var client = factory.CreateClient();

        var request = new CreateBookRequest(titulo, isbn, autor, exemplares);

        using var response = await client.PostAsJsonAsync(new Uri("/books", UriKind.Relative), request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.CodigoDoProblemaAsync(Ct)).ShouldBe(codigoEsperado);
    }

    [Fact]
    public async Task Deve_consultar_um_livro_por_id()
    {
        using var client = factory.CreateClient();
        var criado = await client.CriarLivroAsync(exemplares: 7, cancellationToken: Ct);

        var lido = await client.GetFromJsonAsync<BookResponse>(new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);

        lido!.Id.ShouldBe(criado.Id);
        lido.TotalCopies.ShouldBe(7);
        lido.AvailableCopies.ShouldBe(7);
        lido.ActiveLoans.ShouldBe(0);
    }

    [Fact]
    public async Task Livro_inexistente_deve_responder_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/books/{Guid.CreateVersion7()}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.CodigoDoProblemaAsync(Ct)).ShouldBe(BookErrors.NotFound);
    }

    [Fact]
    public async Task Deve_buscar_por_titulo_e_por_autor()
    {
        using var client = factory.CreateClient();
        var marcador = $"Zx{Random.Shared.Next(100_000, 999_999)}";

        await client.CriarLivroAsync(titulo: $"Memorias {marcador}", cancellationToken: Ct);
        await client.CriarLivroAsync(autor: $"Autora {marcador}", cancellationToken: Ct);
        await client.CriarLivroAsync(cancellationToken: Ct);

        var porTitulo = await client.GetFromJsonAsync<PagedResult<BookResponse>>(
            new Uri($"/books?q=Memorias {marcador}", UriKind.Relative), Ct);

        porTitulo!.Items.Count.ShouldBe(1);

        // A busca cobre titulo OU autor, e o marcador aparece nos dois livros criados aqui.
        var porMarcador = await client.GetFromJsonAsync<PagedResult<BookResponse>>(
            new Uri($"/books?q={marcador}", UriKind.Relative), Ct);

        porMarcador!.TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task Deve_paginar_a_listagem()
    {
        using var client = factory.CreateClient();

        var pagina = await client.GetFromJsonAsync<PagedResult<BookResponse>>(
            new Uri("/books?page=1&pageSize=2", UriKind.Relative), Ct);

        pagina!.PageSize.ShouldBe(2);
        pagina.Items.Count.ShouldBeLessThanOrEqualTo(2);

        // pageSize acima do teto e reduzido em vez de aceito.
        var limitada = await client.GetFromJsonAsync<PagedResult<BookResponse>>(
            new Uri("/books?pageSize=5000", UriKind.Relative), Ct);

        limitada!.PageSize.ShouldBe(SearchBooksQuery.MaxPageSize);
    }

    [Fact]
    public async Task Deve_alterar_um_livro_e_incrementar_a_versao()
    {
        using var client = factory.CreateClient();
        var criado = await client.CriarLivroAsync(exemplares: 3, cancellationToken: Ct);

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/books/{criado.Id}", UriKind.Relative),
            new UpdateBookRequest("Titulo corrigido", null, null, null, criado.Version),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var alterado = await response.Content.ReadFromJsonAsync<BookResponse>(Ct);

        alterado!.Title.ShouldBe("Titulo corrigido");
        alterado.Isbn.ShouldBe(criado.Isbn, "campo ausente no PATCH permanece inalterado");
        alterado.Version.ShouldBe(criado.Version + 1);
    }

    [Fact]
    public async Task Ajustar_a_quantidade_move_a_disponibilidade_pelo_mesmo_delta()
    {
        using var client = factory.CreateClient();
        var criado = await client.CriarLivroAsync(exemplares: 3, cancellationToken: Ct);

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/books/{criado.Id}", UriKind.Relative),
            new UpdateBookRequest(null, null, null, 6, criado.Version),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var alterado = await response.Content.ReadFromJsonAsync<BookResponse>(Ct);

        alterado!.TotalCopies.ShouldBe(6);
        alterado.AvailableCopies.ShouldBe(6);
    }

    // Regressao: alterar descritivos E quantidade no mesmo PATCH fazia a entidade em
    // memoria incrementar Version duas vezes (um Touch por metodo do dominio) enquanto o
    // UPDATE gravava +1. A resposta saia um passo a frente do banco, e o cliente que
    // usasse essa versao no PATCH seguinte levava um 409 espurio.
    [Fact]
    public async Task Patch_que_altera_descritivos_e_quantidade_deve_devolver_a_versao_do_banco()
    {
        using var client = factory.CreateClient();
        var criado = await client.CriarLivroAsync(exemplares: 2, cancellationToken: Ct);

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/books/{criado.Id}", UriKind.Relative),
            new UpdateBookRequest("Titulo novo", null, "Autor novo", 5, criado.Version),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var devolvido = await response.Content.ReadFromJsonAsync<BookResponse>(Ct);
        var noBanco = await client.GetFromJsonAsync<BookResponse>(
            new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);

        devolvido!.Version.ShouldBe(criado.Version + 1, "um PATCH e uma versao, quantos campos forem");
        devolvido.Version.ShouldBe(noBanco!.Version, "a resposta nao pode divergir do banco");
        devolvido.TotalCopies.ShouldBe(noBanco.TotalCopies);
        devolvido.AvailableCopies.ShouldBe(noBanco.AvailableCopies);

        // E a versao devolvida tem de servir para o proximo PATCH.
        using var seguinte = await client.PatchAsJsonAsync(
            new Uri($"/books/{criado.Id}", UriKind.Relative),
            new UpdateBookRequest("Mais um titulo", null, null, null, devolvido.Version),
            Ct);

        seguinte.StatusCode.ShouldBe(HttpStatusCode.OK, "a versao devolvida precisa ser utilizavel");
    }

    // Caminho otimista da secao 2.4: o cliente envia a versao que leu e o UPDATE so
    // acontece se ninguem tiver alterado o livro nesse meio tempo.
    [Fact]
    public async Task Versao_desatualizada_no_patch_deve_responder_409()
    {
        using var client = factory.CreateClient();
        var criado = await client.CriarLivroAsync(cancellationToken: Ct);
        var versaoObsoleta = criado.Version;

        using var primeiro = await client.PatchAsJsonAsync(
            new Uri($"/books/{criado.Id}", UriKind.Relative),
            new UpdateBookRequest("Primeira edicao", null, null, null, versaoObsoleta),
            Ct);

        primeiro.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var segundo = await client.PatchAsJsonAsync(
            new Uri($"/books/{criado.Id}", UriKind.Relative),
            new UpdateBookRequest("Segunda edicao", null, null, null, versaoObsoleta),
            Ct);

        segundo.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await segundo.CodigoDoProblemaAsync(Ct)).ShouldBe(BookErrors.ConcurrentModification);

        var atual = await client.GetFromJsonAsync<BookResponse>(new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);
        atual!.Title.ShouldBe("Primeira edicao", "a edicao perdedora nao pode sobrescrever a vencedora");
    }

    [Fact]
    public async Task Delete_deve_desativar_sem_apagar_o_registro()
    {
        using var client = factory.CreateClient().ComAtor("bibliotecaria");
        var criado = await client.CriarLivroAsync(cancellationToken: Ct);

        using var response = await client.DeleteAsync(new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var apos = await client.GetFromJsonAsync<BookResponse>(new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);

        apos!.IsActive.ShouldBeFalse();
        apos.TotalCopies.ShouldBe(criado.TotalCopies, "desativar nao apaga nem zera o acervo");
        apos.DeactivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Livro_desativado_nao_aparece_na_listagem_padrao()
    {
        using var client = factory.CreateClient();
        var marcador = $"Inativo{Random.Shared.Next(100_000, 999_999)}";
        var criado = await client.CriarLivroAsync(titulo: marcador, cancellationToken: Ct);

        using var delete = await client.DeleteAsync(new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var padrao = await client.GetFromJsonAsync<PagedResult<BookResponse>>(
            new Uri($"/books?q={marcador}", UriKind.Relative), Ct);

        padrao!.TotalCount.ShouldBe(0);

        var comInativos = await client.GetFromJsonAsync<PagedResult<BookResponse>>(
            new Uri($"/books?q={marcador}&includeInactive=true", UriKind.Relative), Ct);

        comInativos!.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task Deve_registrar_a_trilha_de_auditoria_das_operacoes_de_catalogo()
    {
        using var client = factory.CreateClient().ComAtor("auditor-teste");
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "corr-catalogo");

        var criado = await client.CriarLivroAsync(cancellationToken: Ct);

        using var patch = await client.PatchAsJsonAsync(
            new Uri($"/books/{criado.Id}", UriKind.Relative),
            new UpdateBookRequest("Novo titulo", null, null, null, criado.Version),
            Ct);
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var delete = await client.DeleteAsync(new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookRentDbContext>();

        var eventos = await dbContext.AuditEvents
            .AsNoTracking()
            .Where(evento => evento.EntityId == criado.Id)
            .OrderBy(evento => evento.Id)
            .ToListAsync(Ct);

        eventos.Select(evento => evento.Action).ShouldBe(
            [AuditActions.BookCreated, AuditActions.BookUpdated, AuditActions.BookDeactivated]);

        eventos.ShouldAllBe(evento => evento.Actor == "auditor-teste");
        eventos.ShouldAllBe(evento => evento.CorrelationId == "corr-catalogo");
        eventos.ShouldAllBe(evento => evento.EntityType == AuditEntityTypes.Book);
        eventos.ShouldAllBe(evento => evento.OccurredAt.Offset == TimeSpan.Zero);

        // O evento de alteracao precisa carregar o suficiente para entender a mudanca.
        var alteracao = eventos.Single(evento => evento.Action == AuditActions.BookUpdated);
        alteracao.Data.ShouldContain("Novo titulo");
    }

    // O RedisCacheStore trata falha de cache como degradacao e engole o erro — entao os
    // demais testes passariam ate com o Redis morto. Este verifica a chave no proprio
    // Redis, garantindo que o cache-aside esta mesmo ativo, e que a escrita o invalida.
    [Fact]
    public async Task Deve_popular_o_cache_na_leitura_e_invalidar_na_escrita()
    {
        using var client = factory.CreateClient();
        var criado = await client.CriarLivroAsync(exemplares: 5, cancellationToken: Ct);

        var redis = factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var chave = $"bookrent:{CacheKeys.Book(criado.Id)}";

        (await redis.KeyExistsAsync(chave)).ShouldBeFalse("a criacao invalida, nao popula");

        await client.GetFromJsonAsync<BookResponse>(new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);

        (await redis.KeyExistsAsync(chave)).ShouldBeTrue("a leitura popula o snapshot");

        using var patch = await client.PatchAsJsonAsync(
            new Uri($"/books/{criado.Id}", UriKind.Relative),
            new UpdateBookRequest(null, null, null, 9, criado.Version),
            Ct);
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await redis.KeyExistsAsync(chave)).ShouldBeFalse("a escrita invalida a chave apos o commit");

        var relido = await client.GetFromJsonAsync<BookResponse>(new Uri($"/books/{criado.Id}", UriKind.Relative), Ct);
        relido!.TotalCopies.ShouldBe(9, "a releitura vem da fonte de verdade, nao do valor obsoleto");
    }

    // O UseExceptionHandler limpa a resposta antes de reexecutar o pipeline. Se a
    // correlacao fosse lida do cabecalho nesse ponto, toda resposta de erro sairia com
    // correlationId vazio — exatamente onde ele mais serve para investigar.
    [Fact]
    public async Task Resposta_de_erro_deve_carregar_o_correlation_id_no_corpo_e_no_cabecalho()
    {
        using var client = factory.CreateClient();
        const string Correlacao = "corr-erro-12345";

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/books/{Guid.CreateVersion7()}", UriKind.Relative));
        request.Headers.Add("X-Correlation-Id", Correlacao);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.GetValues("X-Correlation-Id").ShouldContain(Correlacao);

        var problema = await response.Content.ReadFromJsonAsync<ProblemaCompleto>(Ct);

        problema!.CorrelationId.ShouldBe(Correlacao);
        problema.Code.ShouldBe(BookErrors.NotFound);
    }

    private sealed record ProblemaCompleto(string? Code, string? CorrelationId, string? Detail, int Status);

    [Fact]
    public async Task A_trilha_de_auditoria_e_append_only()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookRentDbContext>();

        var evento = AuditEvent.Record(
            AuditEntityTypes.Book,
            Guid.CreateVersion7(),
            AuditActions.BookCreated,
            "teste",
            "corr",
            "{}",
            DateTimeOffset.UtcNow);

        dbContext.AuditEvents.Add(evento);
        await dbContext.SaveChangesAsync(Ct);

        dbContext.AuditEvents.Remove(evento);

        await Should.ThrowAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync(Ct));
    }
}
