using BookRent.Domain.Common;
using BookRent.Domain.Loans;
using Shouldly;

namespace BookRent.UnitTests.Domain;

/// <summary>
/// Maquina de estados do emprestimo. O registro nunca e apagado: devolucao e
/// cancelamento so mudam o status, e o historico permanece consultavel.
/// </summary>
public class LoanTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Quatorze = TimeSpan.FromDays(14);
    private static readonly Guid LivroId = Guid.CreateVersion7();
    private static readonly Guid UsuarioId = Guid.CreateVersion7();

    private static Loan UmEmprestimo() => Loan.Create(LivroId, UsuarioId, "bibliotecaria", Now, Quatorze);

    [Fact]
    public void Nasce_ativo_com_a_data_prevista_de_devolucao_calculada()
    {
        var emprestimo = UmEmprestimo();

        emprestimo.Status.ShouldBe(LoanStatus.Active);
        emprestimo.IsActive.ShouldBeTrue();
        emprestimo.LoanedAt.ShouldBe(Now);
        emprestimo.DueAt.ShouldBe(Now.AddDays(14));
        emprestimo.ReturnedAt.ShouldBeNull();
        emprestimo.CancelledAt.ShouldBeNull();
    }

    [Fact]
    public void Devolver_muda_o_status_e_preserva_o_registro()
    {
        var emprestimo = UmEmprestimo();
        var devolvidoEm = Now.AddDays(3);

        emprestimo.Return(devolvidoEm);

        emprestimo.Status.ShouldBe(LoanStatus.Returned);
        emprestimo.ReturnedAt.ShouldBe(devolvidoEm);
        emprestimo.LoanedAt.ShouldBe(Now, "a data original do emprestimo nao pode ser perdida");
        emprestimo.BookId.ShouldBe(LivroId);
        emprestimo.UserId.ShouldBe(UsuarioId);
    }

    [Fact]
    public void Cancelar_muda_o_status_e_preserva_o_registro()
    {
        var emprestimo = UmEmprestimo();
        var canceladoEm = Now.AddHours(2);

        emprestimo.Cancel(canceladoEm);

        emprestimo.Status.ShouldBe(LoanStatus.Cancelled);
        emprestimo.CancelledAt.ShouldBe(canceladoEm);
        emprestimo.LoanedAt.ShouldBe(Now, "cancelar preserva a informacao de que o emprestimo existiu");
    }

    [Fact]
    public void Nao_deve_devolver_duas_vezes()
    {
        var emprestimo = UmEmprestimo();
        emprestimo.Return(Now);

        var erro = Should.Throw<DomainException>(() => emprestimo.Return(Now.AddDays(1)));

        erro.Code.ShouldBe(LoanErrors.NotActive);
        emprestimo.ReturnedAt.ShouldBe(Now, "a segunda tentativa nao pode sobrescrever a devolucao original");
    }

    [Fact]
    public void Nao_deve_cancelar_um_emprestimo_ja_devolvido()
    {
        var emprestimo = UmEmprestimo();
        emprestimo.Return(Now);

        var erro = Should.Throw<DomainException>(() => emprestimo.Cancel(Now));

        erro.Code.ShouldBe(LoanErrors.NotActive);
        emprestimo.Status.ShouldBe(LoanStatus.Returned);
    }

    [Fact]
    public void Nao_deve_devolver_um_emprestimo_cancelado()
    {
        var emprestimo = UmEmprestimo();
        emprestimo.Cancel(Now);

        var erro = Should.Throw<DomainException>(() => emprestimo.Return(Now));

        erro.Code.ShouldBe(LoanErrors.NotActive);
        emprestimo.Status.ShouldBe(LoanStatus.Cancelled);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(30)]
    public void A_data_prevista_segue_o_periodo_configurado(int dias)
    {
        var emprestimo = Loan.Create(LivroId, UsuarioId, "ator", Now, TimeSpan.FromDays(dias));

        emprestimo.DueAt.ShouldBe(Now.AddDays(dias));
    }

    [Fact]
    public void Deve_rejeitar_periodo_de_emprestimo_nao_positivo()
    {
        var erro = Should.Throw<DomainException>(
            () => Loan.Create(LivroId, UsuarioId, "ator", Now, TimeSpan.Zero));

        erro.Code.ShouldBe(LoanErrors.InvalidLoanPeriod);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deve_exigir_o_ator_para_a_trilha_de_auditoria(string? ator)
    {
        var erro = Should.Throw<DomainException>(
            () => Loan.Create(LivroId, UsuarioId, ator, Now, Quatorze));

        erro.Code.ShouldBe(LoanErrors.ActorRequired);
    }
}
