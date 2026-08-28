using System.Diagnostics.Metrics;

namespace BookRent.Api.Diagnostics;

/// <summary>
/// Metricas de negocio exigidas pelo desafio: emprestimos criados, rejeicoes por
/// indisponibilidade, repeticoes idempotentes e latencia do endpoint de emprestimo.
/// Registrada como singleton e injetada nos casos de uso / endpoints.
/// </summary>
public sealed class LoanMetrics
{
    private readonly Counter<long> _loansCreated;
    private readonly Counter<long> _loansRejected;
    private readonly Counter<long> _idempotentReplays;
    private readonly Histogram<double> _loanRequestDuration;

    public LoanMetrics()
    {
        var meter = BookRentDiagnostics.Meter;

        _loansCreated = meter.CreateCounter<long>(
            "bookrent.loans.created",
            unit: "{loan}",
            description: "Emprestimos criados com sucesso.");

        _loansRejected = meter.CreateCounter<long>(
            "bookrent.loans.rejected",
            unit: "{rejection}",
            description: "Tentativas de emprestimo rejeitadas por regra de negocio.");

        _idempotentReplays = meter.CreateCounter<long>(
            "bookrent.loans.idempotent_replays",
            unit: "{request}",
            description: "Requisicoes reprocessadas que reaproveitaram uma resposta ja produzida.");

        _loanRequestDuration = meter.CreateHistogram<double>(
            "bookrent.loans.request.duration",
            unit: "s",
            description: "Latencia ponta a ponta do endpoint de criacao de emprestimo.");
    }

    public void LoanCreated() => _loansCreated.Add(1);

    /// <param name="reason">Motivo estavel da recusa, ex.: "no_copies_available".</param>
    public void LoanRejected(string reason) =>
        _loansRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void IdempotentReplay() => _idempotentReplays.Add(1);

    public void RecordLoanRequestDuration(double seconds, string outcome) =>
        _loanRequestDuration.Record(seconds, new KeyValuePair<string, object?>("outcome", outcome));
}
