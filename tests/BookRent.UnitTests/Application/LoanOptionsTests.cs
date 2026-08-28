using BookRent.Application.Loans;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace BookRent.UnitTests.Application;

/// <summary>
/// Guarda a configuracao de emprestimo contra um erro silencioso de formato.
///
/// TimeSpan.Parse interpreta "24:00:00" como 24 DIAS, nao 24 horas: quando o primeiro
/// componente passa de 23, ele deixa de ser hora e o formato vira d:hh:mm. Uma chave de
/// idempotencia valida por 24 dias nao quebra nada de forma visivel — so guarda lixo por
/// muito mais tempo e permite replay muito depois do pretendido. Sem teste, ninguem nota.
/// </summary>
public class LoanOptionsTests
{
    [Fact]
    public void O_appsettings_deve_configurar_retencao_de_exatamente_um_dia()
    {
        var options = Bind(("Loans:IdempotencyRetention", "1.00:00:00"));

        options.IdempotencyRetention.ShouldBe(TimeSpan.FromHours(24));
        options.IdempotencyRetention.ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public void O_formato_hh_mm_ss_acima_de_23_horas_e_lido_como_dias()
    {
        // Documenta a armadilha: este e o valor que estava no appsettings.json.
        var options = Bind(("Loans:IdempotencyRetention", "24:00:00"));

        options.IdempotencyRetention.ShouldBe(TimeSpan.FromDays(24), "24 dias, e nao 24 horas");
    }

    [Fact]
    public void Sem_configuracao_valem_os_padroes_do_codigo()
    {
        var options = Bind();

        options.IdempotencyRetention.ShouldBe(TimeSpan.FromHours(24));
        options.DefaultLoanPeriodDays.ShouldBe(14);
        options.LoanPeriod.ShouldBe(TimeSpan.FromDays(14));
    }

    [Fact]
    public void O_prazo_de_emprestimo_deve_seguir_a_configuracao()
    {
        var options = Bind(("Loans:DefaultLoanPeriodDays", "7"));

        options.LoanPeriod.ShouldBe(TimeSpan.FromDays(7));
    }

    private static LoanOptions Bind(params (string Key, string Value)[] valores)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(valores.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

        var options = new LoanOptions();
        configuration.GetSection(LoanOptions.SectionName).Bind(options);

        return options;
    }
}
