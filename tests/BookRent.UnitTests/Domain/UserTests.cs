using BookRent.Domain.Common;
using BookRent.Domain.Users;
using Shouldly;

namespace BookRent.UnitTests.Domain;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Deve_registrar_um_leitor_com_os_dados_normalizados()
    {
        var usuario = User.Register("  Maria Silva  ", "  Maria.Silva@Exemplo.COM ", Now);

        usuario.Name.ShouldBe("Maria Silva");
        usuario.Email.ShouldBe("maria.silva@exemplo.com", "o e-mail e a chave de unicidade e precisa ser canonico");
        usuario.CreatedAt.ShouldBe(Now);
        usuario.Id.ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deve_exigir_o_nome(string? nome)
    {
        var erro = Should.Throw<DomainException>(() => User.Register(nome, "maria@exemplo.com", Now));

        erro.Code.ShouldBe(UserErrors.NameRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sem-arroba")]
    [InlineData("@exemplo.com")]
    [InlineData("maria@")]
    [InlineData("maria@exemplo")]
    [InlineData("maria@@exemplo.com")]
    [InlineData("maria com espaco@exemplo.com")]
    [InlineData("maria@exemplo.")]
    public void Deve_rejeitar_email_mal_formado(string? email)
    {
        var erro = Should.Throw<DomainException>(() => User.Register("Maria", email, Now));

        erro.Code.ShouldBe(UserErrors.EmailInvalid);
    }
}
