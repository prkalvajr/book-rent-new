namespace BookRent.Infrastructure.Health;

/// <summary>
/// Tags que separam os dois endpoints de health.
/// <c>live</c> so responde pelo processo; <c>ready</c> responde pelas dependencias.
/// </summary>
public static class HealthCheckTags
{
    public const string Live = "live";
    public const string Ready = "ready";
}
