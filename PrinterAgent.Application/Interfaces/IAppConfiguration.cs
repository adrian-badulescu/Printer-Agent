using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IAppConfiguration
{
    /// <summary>Opțional după enroll; poate rămâne gol dacă restaurantul vine din sesiune.</summary>
    string RestaurantId { get; }

    /// <summary>Cod temporar de provisioning (din manager); șters după primul enroll reușit.</summary>
    string? EnrollmentCode { get; }

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

    /// <summary>Timeout TCP la conectarea la imprimantă (secunde); evită blocarea în status Printing minute întregi.</summary>
    int PrinterConnectTimeoutSeconds { get; }
}
