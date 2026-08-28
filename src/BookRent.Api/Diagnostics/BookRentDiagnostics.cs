using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BookRent.Api.Diagnostics;

/// <summary>Nomes dos instrumentos OpenTelemetry expostos pela aplicacao.</summary>
public static class BookRentDiagnostics
{
    public const string ServiceName = "bookrent-api";
    public const string ActivitySourceName = "BookRent.Api";
    public const string MeterName = "BookRent.Api";

    /// <summary>Fonte de spans para instrumentacao manual dos casos de uso.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);
}
