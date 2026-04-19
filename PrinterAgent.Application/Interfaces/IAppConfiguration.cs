using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IAppConfiguration
{
    string RestaurantId { get; }
    string BackendUrl { get; }
    string BackendJwtToken { get; }
    string RedisConnectionString { get; }

    /// <summary>Prefix stream Redis (backend trebuie să folosească același): cheia = {prefix}.{restaurantId}.</summary>
    string RedisStreamKeyPrefix { get; }

    /// <summary>Grupul consumer XREADGROUP (același ca la toți agenții care partajă cozi).</summary>
    string RedisConsumerGroup { get; }

    /// <summary>Conexiune Redis mascată pentru loguri (fără parolă).</summary>
    string RedisConnectionSummary { get; }

    List<Printer> Printers { get; }
    string Version { get; }
    /// <summary>Secret partajat cu backend pentru HMAC la update (aceeași valoare ca PrinterAgent:UpdateSignatureSecret).</summary>
    string UpdateSignatureSecret { get; }
    int MaxPrintRetryAttempts { get; }
    int PrintRetryBaseDelayMs { get; }
}
