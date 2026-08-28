using BookRent.IntegrationTests.Fixtures;

namespace BookRent.IntegrationTests;

/// <summary>
/// Cenarios centrais do desafio, a serem implementados junto com o dominio:
/// disputa pelo ultimo exemplar e idempotencia do POST /loans.
/// </summary>
[Collection(IntegrationTestSuite.Name)]
public class ConcurrencyTests(BookRentApiFactory factory)
{
    [Fact(Skip = "Pendente: implementar apos o caso de uso de emprestimo.")]
    public Task Duas_requisicoes_simultaneas_para_o_ultimo_exemplar_devem_gerar_um_unico_emprestimo()
    {
        _ = factory;
        return Task.CompletedTask;
    }

    [Fact(Skip = "Pendente: implementar apos o caso de uso de emprestimo.")]
    public Task Repetir_a_mesma_idempotency_key_nao_pode_criar_um_segundo_emprestimo()
    {
        _ = factory;
        return Task.CompletedTask;
    }

    [Fact(Skip = "Pendente: implementar apos o caso de uso de devolucao/cancelamento.")]
    public Task Devolucao_e_cancelamento_devem_preservar_o_historico_do_emprestimo()
    {
        _ = factory;
        return Task.CompletedTask;
    }
}
