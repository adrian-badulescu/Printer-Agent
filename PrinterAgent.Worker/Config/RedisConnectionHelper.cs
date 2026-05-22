using System.Text.RegularExpressions;

namespace PrinterAgent.Worker.Config;

internal static partial class RedisConnectionHelper
{
    /// <summary>
    /// Maschează password= / token= pentru logging.
    /// </summary>
    public static string RedactForLogs(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return string.Empty;

        var s = PasswordTokenRegex().Replace(connectionString, "$1=***");
        return s;
    }

    [GeneratedRegex("(?i)(password|token)=([^,]+)", RegexOptions.Compiled)]
    private static partial Regex PasswordTokenRegex();

    /// <summary>Adaugă <c>abortConnect=</c> dacă lipsește (semantica StackExchange.Redis).</summary>
    public static string EnsureAbortConnect(string connectionString, bool abortConnect)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        if (connectionString.Contains("abortConnect=", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var suffix = abortConnect ? "abortConnect=true" : "abortConnect=false";
        return connectionString.TrimEnd().EndsWith(',') ? $"{connectionString}{suffix}" : $"{connectionString},{suffix}";
    }
}
