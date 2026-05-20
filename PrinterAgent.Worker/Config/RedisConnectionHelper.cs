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

    /// <summary>
    /// StackExchange.Redis treats <c>#</c> as start of an inline comment in unquoted values.
    /// Passwords containing <c>#</c> (e.g. <c>secret##</c>) must be quoted or AUTH fails with NOAUTH.
    /// </summary>
    public static string QuoteConnectionValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (value.IndexOfAny(new[] { ',', '=', '#', '"' }) < 0)
            return value;

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

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
