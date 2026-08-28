using System.Net;
using System.Net.Http.Json;
using BookRent.Application.Books;
using BookRent.Application.Loans;
using BookRent.Application.Users;
using BookRent.Domain.Books;
using BookRent.Domain.Loans;
using BookRent.Infrastructure.Persistence;
using BookRent.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace BookRent.IntegrationTests;

/// <summary>
/// Cenarios centrais do desafio, contra PostgreSQL real: disputa pelo ultimo exemplar,
/// idempotencia do POST /loans e preservacao do historico.
///
/// Honestidade sobre estes testes: um teste de corrida e probabilistico. Passar uma vez
/// nao PROVA ausencia de condicao de corrida, so mostra que naquela execucao nao houve.
/// Por isso o cenario do ultimo exemplar e repetido varias vezes e as asseveracoes sao
/// sobre o ESTADO FINAL do banco, nunca sobre ordem ou tempo. A garantia real vem do
/// UPDATE condicional e da CHECK constraint; o teste e evidencia, nao prova.
/// </summary>
[Collection(IntegrationTestSuite.Name)]
public class ConcurrencyTests(BookRentApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(20)]
    public async Task Requisicoes_simultaneas_pelo_ultimo_exemplar_devem_gerar_um_unico_emprestimo(int concorrentes)
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(exemplares: 1, cancellationToken: Ct);

        // Largada comum: todas as tarefas esperam o mesmo sinal antes de disparar.
        using var largada = new SemaphoreSlim(0, concorrentes);

        var tentativas = Enumerable.Range(0, concorrentes)
            .Select(async _ =>
            {
                await largada.WaitAsync(Ct);

                return await client.TentarEmprestarAsync(livro.Id, leitor.Id, Guid.CreateVersion7().ToString(), Ct);
            })
            .ToArray();

        largada.Release(concorrentes);

        var respostas = await Task.WhenAll(tentativas);

        try
        {
            var criados = respostas.Count(r => r.StatusCode == HttpStatusCode.Created);
            var recusados = respostas.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToArray();

            criados.ShouldBe(1, "exatamente um emprestimo pode vencer a disputa pelo ultimo exemplar");
            recusados.Length.ShouldBe(concorrentes - 1);

            foreach (var recusa in recusados)
            {
                (await recusa.CodigoDoProblemaAsync(Ct))
                    .ShouldBe(LoanErrors.NoCopiesAvailable, "a recusa precisa ser clara e de regra de negocio");
            }

            // O que de fato importa: o estado final do banco.
            var disponibilidade = await client.GetFromJsonAsync<BookAvailabilityResponse>(
                new Uri($"/books/{livro.Id}/availability", UriKind.Relative), Ct);

            disponibilidade!.AvailableCopies.ShouldBe(0, "nunca pode ficar negativo nem sobrar exemplar");
            disponibilidade.ActiveLoans.ShouldBe(1);

            using var scope = factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<BookRentDbContext>();

            var emprestimos = await dbContext.Loans
                .AsNoTracking()
                .Where(loan => loan.BookId == livro.Id)
                .ToListAsync(Ct);

            emprestimos.Count.ShouldBe(1, "nenhum emprestimo duplicado pode ter sido gravado");
        }
        finally
        {
            foreach (var resposta in respostas)
            {
                resposta.Dispose();
            }
        }
    }

    [Fact]
    public async Task Repetir_a_mesma_idempotency_key_nao_pode_criar_um_segundo_emprestimo()
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(exemplares: 5, cancellationToken: Ct);
        var chave = Guid.CreateVersion7().ToString();

        using var primeira = await client.TentarEmprestarAsync(livro.Id, leitor.Id, chave, Ct);
        primeira.StatusCode.ShouldBe(HttpStatusCode.Created);
        primeira.Headers.GetValues(LoanEndpointsHeaders.Replayed).ShouldContain("false");
        var original = await primeira.Content.ReadFromJsonAsync<LoanResponse>(Ct);

        using var segunda = await client.TentarEmprestarAsync(livro.Id, leitor.Id, chave, Ct);

        segunda.StatusCode.ShouldBe(HttpStatusCode.Created, "o replay devolve a resposta previamente produzida");
        segunda.Headers.GetValues(LoanEndpointsHeaders.Replayed).ShouldContain("true");

        var repetida = await segunda.Content.ReadFromJsonAsync<LoanResponse>(Ct);
        repetida!.Id.ShouldBe(original!.Id, "e o mesmo emprestimo, nao um novo");
        repetida.LoanedAt.ShouldBe(original.LoanedAt);

        var disponibilidade = await client.GetFromJsonAsync<BookAvailabilityResponse>(
            new Uri($"/books/{livro.Id}/availability", UriKind.Relative), Ct);

        disponibilidade!.AvailableCopies.ShouldBe(4, "a disponibilidade nao pode ser decrementada duas vezes");
    }

    [Fact]
    public async Task Mesma_chave_com_corpo_diferente_deve_ser_recusada_com_422()
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var outroLeitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(exemplares: 5, cancellationToken: Ct);
        var chave = Guid.CreateVersion7().ToString();

        using var primeira = await client.TentarEmprestarAsync(livro.Id, leitor.Id, chave, Ct);
        primeira.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var conflitante = await client.TentarEmprestarAsync(livro.Id, outroLeitor.Id, chave, Ct);

        conflitante.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await conflitante.CodigoDoProblemaAsync(Ct)).ShouldBe(LoanErrors.IdempotencyKeyReused);
    }

    // Aqui as duas garantias se cruzam: mesma chave E concorrencia. O indice unico da
    // tabela de idempotencia bloqueia a segunda ate a primeira commitar.
    [Fact]
    public async Task Duas_requisicoes_simultaneas_com_a_mesma_chave_devem_gerar_um_unico_emprestimo()
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(exemplares: 5, cancellationToken: Ct);
        var chave = Guid.CreateVersion7().ToString();

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => client.TentarEmprestarAsync(livro.Id, leitor.Id, chave, Ct)));

        try
        {
            respostas.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);

            var emprestimos = await Task.WhenAll(
                respostas.Select(r => r.Content.ReadFromJsonAsync<LoanResponse>(Ct)));

            emprestimos.Select(l => l!.Id).Distinct().Count()
                .ShouldBe(1, "todas as respostas descrevem o mesmo emprestimo");

            var disponibilidade = await client.GetFromJsonAsync<BookAvailabilityResponse>(
                new Uri($"/books/{livro.Id}/availability", UriKind.Relative), Ct);

            disponibilidade!.AvailableCopies.ShouldBe(4, "so um decremento pode ter acontecido");
        }
        finally
        {
            foreach (var resposta in respostas)
            {
                resposta.Dispose();
            }
        }
    }

    [Fact]
    public async Task Emprestimo_sem_idempotency_key_deve_responder_400()
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(cancellationToken: Ct);

        using var response = await client.PostAsJsonAsync(
            new Uri("/loans", UriKind.Relative),
            new CreateLoanRequest(livro.Id, leitor.Id),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.CodigoDoProblemaAsync(Ct)).ShouldBe(LoanErrors.IdempotencyKeyRequired);
    }

    [Fact]
    public async Task Devolucao_e_cancelamento_devem_preservar_o_historico_do_emprestimo()
    {
        using var client = factory.CreateClient().ComAtor("bibliotecaria");
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(exemplares: 3, cancellationToken: Ct);

        var devolvido = await client.EmprestarAsync(livro.Id, leitor.Id, Ct);
        var cancelado = await client.EmprestarAsync(livro.Id, leitor.Id, Ct);
        var ativo = await client.EmprestarAsync(livro.Id, leitor.Id, Ct);

        using var devolucao = await client.PostAsync(
            new Uri($"/loans/{devolvido.Id}/return", UriKind.Relative), null, Ct);
        devolucao.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var cancelamento = await client.PostAsync(
            new Uri($"/loans/{cancelado.Id}/cancel", UriKind.Relative), null, Ct);
        cancelamento.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Os tres continuam no historico do livro, com o status final.
        var historico = await client.GetFromJsonAsync<PagedResult<LoanResponse>>(
            new Uri($"/books/{livro.Id}/history", UriKind.Relative), Ct);

        historico!.TotalCount.ShouldBe(3, "devolver e cancelar nao podem apagar o registro");

        historico.Items.Single(l => l.Id == devolvido.Id).Status.ShouldBe(nameof(LoanStatus.Returned));
        historico.Items.Single(l => l.Id == cancelado.Id).Status.ShouldBe(nameof(LoanStatus.Cancelled));
        historico.Items.Single(l => l.Id == ativo.Id).Status.ShouldBe(nameof(LoanStatus.Active));

        historico.Items.Single(l => l.Id == devolvido.Id).ReturnedAt.ShouldNotBeNull();
        historico.Items.Single(l => l.Id == cancelado.Id).CancelledAt.ShouldNotBeNull();

        // E no historico do leitor tambem.
        var doLeitor = await client.GetFromJsonAsync<PagedResult<LoanResponse>>(
            new Uri($"/users/{leitor.Id}/loans", UriKind.Relative), Ct);
        doLeitor!.TotalCount.ShouldBe(3);

        // Devolver e cancelar recolocaram os exemplares em circulacao.
        var disponibilidade = await client.GetFromJsonAsync<BookAvailabilityResponse>(
            new Uri($"/books/{livro.Id}/availability", UriKind.Relative), Ct);
        disponibilidade!.AvailableCopies.ShouldBe(2);
        disponibilidade.ActiveLoans.ShouldBe(1);

        // Desativar o livro tambem nao pode apagar nada — mas so depois que nao houver ativo.
        using var recusa = await client.DeleteAsync(new Uri($"/books/{livro.Id}", UriKind.Relative), Ct);
        recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await recusa.CodigoDoProblemaAsync(Ct)).ShouldBe(BookErrors.HasActiveLoans);

        using var devolveUltimo = await client.PostAsync(
            new Uri($"/loans/{ativo.Id}/return", UriKind.Relative), null, Ct);
        devolveUltimo.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var desativa = await client.DeleteAsync(new Uri($"/books/{livro.Id}", UriKind.Relative), Ct);
        desativa.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var apos = await client.GetFromJsonAsync<PagedResult<LoanResponse>>(
            new Uri($"/books/{livro.Id}/history", UriKind.Relative), Ct);
        apos!.TotalCount.ShouldBe(3, "o historico sobrevive a desativacao do livro");
    }

    [Fact]
    public async Task Devolver_duas_vezes_deve_responder_409_e_nao_duplicar_a_disponibilidade()
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(exemplares: 2, cancellationToken: Ct);
        var emprestimo = await client.EmprestarAsync(livro.Id, leitor.Id, Ct);

        using var primeira = await client.PostAsync(
            new Uri($"/loans/{emprestimo.Id}/return", UriKind.Relative), null, Ct);
        primeira.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var segunda = await client.PostAsync(
            new Uri($"/loans/{emprestimo.Id}/return", UriKind.Relative), null, Ct);

        segunda.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await segunda.CodigoDoProblemaAsync(Ct)).ShouldBe(LoanErrors.NotActive);

        var disponibilidade = await client.GetFromJsonAsync<BookAvailabilityResponse>(
            new Uri($"/books/{livro.Id}/availability", UriKind.Relative), Ct);

        disponibilidade!.AvailableCopies.ShouldBe(2, "a segunda devolucao nao pode incrementar de novo");
    }

    [Fact]
    public async Task Nao_deve_emprestar_livro_desativado()
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(cancellationToken: Ct);

        using var delete = await client.DeleteAsync(new Uri($"/books/{livro.Id}", UriKind.Relative), Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var tentativa = await client.TentarEmprestarAsync(
            livro.Id, leitor.Id, Guid.CreateVersion7().ToString(), Ct);

        tentativa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await tentativa.CodigoDoProblemaAsync(Ct)).ShouldBe(BookErrors.Inactive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deve_responder_404_para_livro_ou_leitor_inexistente(bool livroInexistente)
    {
        using var client = factory.CreateClient();
        var leitor = await client.CriarLeitorAsync(Ct);
        var livro = await client.CriarLivroAsync(cancellationToken: Ct);

        var bookId = livroInexistente ? Guid.CreateVersion7() : livro.Id;
        var userId = livroInexistente ? leitor.Id : Guid.CreateVersion7();

        using var response = await client.TentarEmprestarAsync(
            bookId, userId, Guid.CreateVersion7().ToString(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Uma requisicao que falha faz rollback e LIBERA a chave, em vez de queima-la:
        // a mesma chave volta a funcionar depois que a causa e corrigida.
        var chave = Guid.CreateVersion7().ToString();

        using var falha = await client.TentarEmprestarAsync(Guid.CreateVersion7(), leitor.Id, chave, Ct);
        falha.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var sucesso = await client.TentarEmprestarAsync(livro.Id, leitor.Id, chave, Ct);
        sucesso.StatusCode.ShouldBe(HttpStatusCode.Created, "o rollback liberou a chave");
    }

    private static class LoanEndpointsHeaders
    {
        public const string Replayed = "Idempotency-Replayed";
    }
}
