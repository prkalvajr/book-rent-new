using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Books;
using BookRent.Infrastructure.Persistence;
using BookRent.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace BookRent.IntegrationTests;

/// <summary>
/// Regressao do defeito mais grave encontrado no code review.
///
/// O EnableRetryOnFailure faz o ExecutionStrategy reexecutar o delegate no MESMO
/// DbContext. O rollback desfaz a transacao no banco, mas NAO limpa o ChangeTracker:
/// sem um Clear() por tentativa, as entidades da tentativa que falhou continuam em
/// estado Added e sao gravadas junto com as da tentativa seguinte.
///
/// No caminho de emprestimo isso produzia DOIS emprestimos com UM decremento de
/// disponibilidade — o "emprestimo duplicado" que o desafio proibe, chegando por um
/// erro transitorio de infraestrutura em vez de por concorrencia. E a CHECK constraint
/// nao pegaria: available_copies continuaria dentro da faixa valida enquanto a
/// invariante total - available = ativos ja estaria quebrada.
/// </summary>
[Collection(IntegrationTestSuite.Name)]
public class UnitOfWorkRetryTests(BookRentApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Simula a falha transitoria que dispara o retry. 53300 (too_many_connections) e
    /// classificada como transitoria pelo Npgsql, e a secao 9.12 do plano descreve
    /// exatamente essa falha como a esperada sob contencao de pool.
    /// </summary>
    private static PostgresException FalhaTransitoria() =>
        new("too many connections", "FATAL", "FATAL", PostgresErrorCodes.TooManyConnections);

    [Fact]
    public async Task Retry_apos_falha_transitoria_nao_pode_gravar_a_entidade_duas_vezes()
    {
        using var scope = factory.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookRentDbContext>();

        var isbn = ApiClientExtensions.NovoIsbn();
        var tentativas = 0;

        var criado = await unitOfWork.ExecuteInTransactionAsync(
            ct =>
            {
                tentativas++;

                // A entidade nasce DENTRO do delegate, como no CreateLoanHandler: cada
                // tentativa produz uma instancia nova, com id proprio.
                var livro = Book.Create($"Retry {isbn}", isbn, "Autor", 3, DateTimeOffset.UtcNow);
                dbContext.Books.Add(livro);

                // A primeira tentativa falha depois de ja ter enfileirado a entidade.
                return tentativas == 1
                    ? throw FalhaTransitoria()
                    : Task.FromResult(livro);
            },
            Ct);

        tentativas.ShouldBeGreaterThan(1, "a falha transitoria precisa ter disparado o retry");

        var gravados = await dbContext.Books
            .AsNoTracking()
            .Where(book => book.Isbn == isbn)
            .ToListAsync(Ct);

        gravados.Count.ShouldBe(1, "o retry nao pode gravar tambem a entidade da tentativa que falhou");
        gravados[0].Id.ShouldBe(criado.Id, "o registro gravado e o da tentativa bem-sucedida");
    }

    /// <summary>
    /// O mesmo defeito no formato em que ele realmente doia: emprestimo mais evento de
    /// auditoria na mesma transacao. Duas entidades enfileiradas, duas duplicatas.
    /// </summary>
    [Fact]
    public async Task Retry_nao_pode_duplicar_entidades_enfileiradas_na_tentativa_anterior()
    {
        using var scope = factory.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookRentDbContext>();

        var marcador = $"Lote{Guid.CreateVersion7():N}";
        var tentativas = 0;

        await unitOfWork.ExecuteInTransactionAsync(
            ct =>
            {
                tentativas++;

                dbContext.Books.Add(
                    Book.Create($"{marcador} A", ApiClientExtensions.NovoIsbn(), "Autor", 1, DateTimeOffset.UtcNow));
                dbContext.Books.Add(
                    Book.Create($"{marcador} B", ApiClientExtensions.NovoIsbn(), "Autor", 1, DateTimeOffset.UtcNow));

                return tentativas < 3
                    ? throw FalhaTransitoria()
                    : Task.FromResult(true);
            },
            Ct);

        tentativas.ShouldBe(3, "duas falhas antes do sucesso");

        var gravados = await dbContext.Books
            .AsNoTracking()
            .Where(book => book.Title.StartsWith(marcador))
            .CountAsync(Ct);

        gravados.ShouldBe(2, "duas tentativas descartadas nao podem deixar seis linhas");
    }
}
