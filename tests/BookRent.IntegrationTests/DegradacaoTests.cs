using System.Net;
using System.Net.Http.Json;
using BookRent.Application.Books;
using BookRent.IntegrationTests.Fixtures;
using Shouldly;

namespace BookRent.IntegrationTests;

/// <summary>
/// Comportamento com dependencia fora do ar. Sao os unicos testes que REMOVEM uma
/// dependencia em vez de exercita-la — sem eles, a resiliencia do cache e a separacao
/// entre liveness e readiness ficam escritas no codigo e nunca verificadas.
///
/// O Redis e suspenso e religado num bloco finally. Toda a suite de integracao vive
/// numa unica colecao, entao os testes rodam em sequencia e a suspensao nao alcanca
/// nenhum teste concorrente.
/// </summary>
[Collection(IntegrationTestSuite.Name)]
public class DegradacaoTests(BookRentApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // O desafio e explicito: /health/live "nao deve falhar apenas porque PostgreSQL ou
    // Redis estao indisponiveis". O teste antigo verificava 200 com tudo no ar, o que
    // nao exercita o requisito. Se o liveness passasse a checar dependencias, uma queda
    // do banco reiniciaria pods saudaveis em cascata.
    [Fact]
    public async Task Com_o_redis_fora_o_live_segue_200_e_o_ready_responde_503()
    {
        using var client = factory.CreateClient();

        await factory.SuspenderRedisAsync();

        try
        {
            using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
            live.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                "liveness responde pelo processo, nao pelas dependencias");

            using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
            ready.StatusCode.ShouldBe(
                HttpStatusCode.ServiceUnavailable,
                "readiness precisa tirar o pod do Service quando o cache nao responde");
        }
        finally
        {
            await factory.ReligarRedisAsync();
        }
    }

    // O RedisCacheStore captura tudo que nao seja cancelamento e devolve o controle ao
    // PostgreSQL. Isso estava escrito no codigo e nunca exercitado: sem este teste, a
    // afirmacao do README de que "falha de Redis e degradacao, nao erro" era so uma
    // afirmacao.
    [Fact]
    public async Task Leitura_deve_continuar_funcionando_com_o_redis_fora()
    {
        using var client = factory.CreateClient();
        var livro = await client.CriarLivroAsync(exemplares: 4, cancellationToken: Ct);

        await factory.SuspenderRedisAsync();

        try
        {
            var lido = await client.GetFromJsonAsync<BookResponse>(
                new Uri($"/books/{livro.Id}", UriKind.Relative), Ct);

            lido!.Id.ShouldBe(livro.Id);
            lido.AvailableCopies.ShouldBe(4, "o dado vem da fonte de verdade, nao do cache");

            var disponibilidade = await client.GetFromJsonAsync<BookAvailabilityResponse>(
                new Uri($"/books/{livro.Id}/availability", UriKind.Relative), Ct);

            disponibilidade!.AvailableCopies.ShouldBe(4);
        }
        finally
        {
            await factory.ReligarRedisAsync();
        }
    }

    // O caminho critico do desafio nao pode depender do cache de forma alguma: o
    // PostgreSQL e a autoridade da decisao de emprestar, e o Redis so serve leitura.
    [Fact]
    public async Task Emprestimo_deve_funcionar_com_o_redis_fora()
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(exemplares: 1, cancellationToken: Ct);

        await factory.SuspenderRedisAsync();

        try
        {
            using var primeiro = await client.TentarEmprestarAsync(
                livro.Id, leitor.Id, Guid.CreateVersion7().ToString(), Ct);

            primeiro.StatusCode.ShouldBe(
                HttpStatusCode.Created,
                "a decisao de emprestar e do banco; o cache nao participa dela");

            // E a regra do ultimo exemplar continua valendo sem cache nenhum.
            using var segundo = await client.TentarEmprestarAsync(
                livro.Id, leitor.Id, Guid.CreateVersion7().ToString(), Ct);

            segundo.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            await factory.ReligarRedisAsync();
        }
    }

    [Fact]
    public async Task Apos_religar_o_redis_o_cache_volta_a_operar()
    {
        using var client = factory.CreateClient();
        var livro = await client.CriarLivroAsync(exemplares: 2, cancellationToken: Ct);

        await factory.SuspenderRedisAsync();

        try
        {
            await client.GetFromJsonAsync<BookResponse>(
                new Uri($"/books/{livro.Id}", UriKind.Relative), Ct);
        }
        finally
        {
            await factory.ReligarRedisAsync();
        }

        // A leitura seguinte precisa voltar a popular normalmente — a degradacao nao
        // pode deixar o multiplexer num estado ruim.
        var lido = await client.GetFromJsonAsync<BookResponse>(
            new Uri($"/books/{livro.Id}", UriKind.Relative), Ct);

        lido!.Id.ShouldBe(livro.Id);

        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK, "o readiness precisa se recuperar sozinho");
    }
}
