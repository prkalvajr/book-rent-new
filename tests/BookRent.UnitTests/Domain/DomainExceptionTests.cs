using BookRent.Domain.Common;
using Shouldly;

namespace BookRent.UnitTests.Domain;

/// <summary>Exemplo de teste unitario puro — sem I/O, sem containers.</summary>
public class DomainExceptionTests
{
    [Fact]
    public void Deve_preservar_o_codigo_da_regra_violada()
    {
        var exception = new DomainException("loan.no_copies_available", "Nao ha exemplares disponiveis.");

        exception.Code.ShouldBe("loan.no_copies_available");
        exception.Message.ShouldBe("Nao ha exemplares disponiveis.");
    }
}
