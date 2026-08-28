using System.Net;
using BookRent.IntegrationTests.Fixtures;
using Shouldly;

namespace BookRent.IntegrationTests;

/// <summary>
/// Smoke test da composicao completa: se este teste passa, a DI, a conexao com
/// PostgreSQL e a conexao com Redis estao corretas.
/// </summary>
[Collection(IntegrationTestSuite.Name)]
public class HealthEndpointsTests(BookRentApiFactory factory)
{
    [Fact]
    public async Task Live_deve_responder_200_apenas_com_o_processo_ativo()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_deve_responder_200_com_postgres_e_redis_disponiveis()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldContain("postgres");
        body.ShouldContain("redis");
    }

    [Fact]
    public async Task Deve_devolver_o_correlation_id_recebido_no_cabecalho_da_resposta()
    {
        using var client = factory.CreateClient();
        const string CorrelationId = "teste-correlation-id";

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add("X-Correlation-Id", CorrelationId);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Correlation-Id").ShouldContain(CorrelationId);
    }
}
