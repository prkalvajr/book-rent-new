using Microsoft.Extensions.Logging;

namespace BookRent.Api.Diagnostics;

/// <summary>
/// Mensagens de log geradas em tempo de compilacao (LoggerMessage source generator):
/// sem boxing de argumentos e sem alocacao quando o nivel esta desligado.
/// </summary>
internal static partial class ApiLog
{
    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Information,
        Message = "Aplicando migrations pendentes do Entity Framework Core")]
    public static partial void ApplyingMigrations(ILogger logger);

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Information,
        Message = "Iniciando {ServiceName} no ambiente {EnvironmentName}")]
    public static partial void Starting(ILogger logger, string serviceName, string environmentName);

    [LoggerMessage(
        EventId = 102,
        Level = LogLevel.Warning,
        Message = "Regra de negocio violada: {DomainErrorCode}")]
    public static partial void DomainRuleViolated(ILogger logger, string domainErrorCode, Exception exception);
}
