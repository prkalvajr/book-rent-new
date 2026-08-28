using BookRent.Domain.Books;
using BookRent.Domain.Common;
using Shouldly;

namespace BookRent.UnitTests.Domain;

/// <summary>
/// Invariantes do catalogo. Sem I/O: o dominio recebe o instante como parametro,
/// entao nao ha relogio para falsificar nem container para subir.
/// </summary>
public class BookTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static Book UmLivroCom(int exemplares = 5) =>
        Book.Create("Dom Casmurro", "978-85-359-1066-3", "Machado de Assis", exemplares, Now);

    [Fact]
    public void Ao_criar_todos_os_exemplares_ficam_disponiveis()
    {
        var livro = UmLivroCom(exemplares: 5);

        livro.TotalCopies.ShouldBe(5);
        livro.AvailableCopies.ShouldBe(5);
        livro.ActiveLoans.ShouldBe(0);
        livro.IsActive.ShouldBeTrue();
        livro.Id.ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("978-85-359-1066-3", "9788535910663")]
    [InlineData("  978 85 359 1066 3  ", "9788535910663")]
    [InlineData("0-306-40615-2", "0306406152")]
    [InlineData("080442957x", "080442957X")]
    public void Deve_normalizar_o_isbn_para_a_forma_canonica(string entrada, string esperado)
    {
        var livro = Book.Create("Titulo", entrada, "Autor", 1, Now);

        livro.Isbn.ShouldBe(esperado);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("97885359106")]
    [InlineData("97885359106X")]
    [InlineData("X306406152")]
    public void Deve_rejeitar_isbn_mal_formado(string? isbn)
    {
        var erro = Should.Throw<DomainException>(() => Book.Create("Titulo", isbn, "Autor", 1, Now));

        erro.Code.ShouldBe(BookErrors.IsbnInvalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deve_exigir_titulo(string? titulo)
    {
        var erro = Should.Throw<DomainException>(() => Book.Create(titulo, "9788535910663", "Autor", 1, Now));

        erro.Code.ShouldBe(BookErrors.TitleRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deve_exigir_autor(string? autor)
    {
        var erro = Should.Throw<DomainException>(() => Book.Create("Titulo", "9788535910663", autor, 1, Now));

        erro.Code.ShouldBe(BookErrors.AuthorRequired);
    }

    [Fact]
    public void Deve_rejeitar_quantidade_negativa_de_exemplares()
    {
        var erro = Should.Throw<DomainException>(
            () => Book.Create("Titulo", "9788535910663", "Autor", -1, Now));

        erro.Code.ShouldBe(BookErrors.TotalCopiesNegative);
    }

    [Fact]
    public void Ajustar_a_quantidade_move_a_disponibilidade_pelo_mesmo_delta()
    {
        var livro = UmLivroCom(exemplares: 5);
        livro.RegisterCheckout();
        livro.RegisterCheckout();

        livro.AvailableCopies.ShouldBe(3);

        livro.AdjustTotalCopies(8, Now);

        livro.TotalCopies.ShouldBe(8);
        livro.AvailableCopies.ShouldBe(6);
        livro.ActiveLoans.ShouldBe(2, "os dois emprestimos continuam ativos apos o ajuste");
    }

    [Fact]
    public void Ajustar_para_baixo_reduz_a_disponibilidade_pelo_mesmo_delta()
    {
        var livro = UmLivroCom(exemplares: 5);
        livro.RegisterCheckout();

        livro.AdjustTotalCopies(3, Now);

        livro.TotalCopies.ShouldBe(3);
        livro.AvailableCopies.ShouldBe(2);
    }

    [Fact]
    public void Nao_deve_reduzir_a_quantidade_abaixo_dos_emprestimos_ativos()
    {
        var livro = UmLivroCom(exemplares: 3);
        livro.RegisterCheckout();
        livro.RegisterCheckout();

        var erro = Should.Throw<DomainException>(() => livro.AdjustTotalCopies(1, Now));

        erro.Code.ShouldBe(BookErrors.TotalBelowActiveLoans);
        livro.TotalCopies.ShouldBe(3, "a recusa nao pode deixar o agregado alterado pela metade");
        livro.AvailableCopies.ShouldBe(1);
    }

    [Fact]
    public void Nao_deve_emprestar_sem_exemplar_disponivel()
    {
        var livro = UmLivroCom(exemplares: 1);
        livro.RegisterCheckout();

        var erro = Should.Throw<DomainException>(livro.RegisterCheckout);

        erro.Code.ShouldBe("loan.no_copies_available");
        livro.AvailableCopies.ShouldBe(0, "a disponibilidade nunca pode ficar negativa");
    }

    [Fact]
    public void Devolver_recoloca_o_exemplar_em_circulacao()
    {
        var livro = UmLivroCom(exemplares: 2);
        livro.RegisterCheckout();

        livro.RegisterReturn();

        livro.AvailableCopies.ShouldBe(2);
        livro.ActiveLoans.ShouldBe(0);
    }

    [Fact]
    public void Nao_deve_devolver_alem_do_acervo()
    {
        var livro = UmLivroCom(exemplares: 2);

        var erro = Should.Throw<DomainException>(livro.RegisterReturn);

        erro.Code.ShouldBe(BookErrors.AvailabilityOverflow);
    }

    // Guarda a decisao da secao 9.7: se emprestimo mexesse no token, todo emprestimo
    // invalidaria a edicao de catalogo em andamento e o bibliotecario levaria 409 em
    // sequencia num livro movimentado.
    [Fact]
    public void Emprestimo_e_devolucao_nao_podem_alterar_o_token_de_concorrencia()
    {
        var livro = UmLivroCom(exemplares: 2);
        var versaoInicial = livro.Version;

        livro.RegisterCheckout();
        livro.RegisterReturn();

        livro.Version.ShouldBe(versaoInicial);
    }

    [Fact]
    public void Alterar_campos_descritivos_incrementa_o_token_de_concorrencia()
    {
        var livro = UmLivroCom();
        var versaoInicial = livro.Version;

        livro.UpdateDetails("Outro titulo", "0306406152", "Outro autor", Now);

        livro.Version.ShouldBe(versaoInicial + 1);
        livro.Title.ShouldBe("Outro titulo");
        livro.Isbn.ShouldBe("0306406152");
        livro.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void Ajuste_sem_mudanca_efetiva_nao_incrementa_o_token()
    {
        var livro = UmLivroCom(exemplares: 5);
        var versaoInicial = livro.Version;

        livro.AdjustTotalCopies(5, Now);

        livro.Version.ShouldBe(versaoInicial);
        livro.UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public void Desativar_preserva_o_registro_e_marca_o_instante()
    {
        var livro = UmLivroCom();

        livro.Deactivate(Now);

        livro.IsActive.ShouldBeFalse();
        livro.DeactivatedAt.ShouldBe(Now);
        livro.TotalCopies.ShouldBe(5, "desativar nao apaga nem zera o acervo");
    }

    [Fact]
    public void Desativar_duas_vezes_e_erro()
    {
        var livro = UmLivroCom();
        livro.Deactivate(Now);

        var erro = Should.Throw<DomainException>(() => livro.Deactivate(Now));

        erro.Code.ShouldBe(BookErrors.AlreadyInactive);
    }

    [Fact]
    public void Livro_desativado_nao_aceita_edicao_nem_emprestimo()
    {
        var livro = UmLivroCom();
        livro.Deactivate(Now);

        Should.Throw<DomainException>(() => livro.UpdateDetails("X", "0306406152", "Y", Now))
            .Code.ShouldBe(BookErrors.Inactive);
        Should.Throw<DomainException>(() => livro.AdjustTotalCopies(9, Now))
            .Code.ShouldBe(BookErrors.Inactive);
        Should.Throw<DomainException>(livro.RegisterCheckout)
            .Code.ShouldBe(BookErrors.Inactive);
    }
}
