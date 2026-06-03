using PrinterAgent.Application.Storage;

namespace PrinterAgent.Infrastructure.Printing;

/// <summary>
/// Loads receipt header ASCII art from ProgramData (operator-editable) or bundled fallback.
/// </summary>
public static class ReceiptHeaderAsciiReader
{
    public const string FileName = "receipt-header.ascii";
    public const int MaxLineWidth = 32;

    public static string ProgramDataPath => Path.Combine(AgentProgramData.Root, FileName);

    public static IReadOnlyList<string> ReadLines(string? programDataPath = null, string? bundledPath = null)
    {
        programDataPath ??= ProgramDataPath;

        if (File.Exists(programDataPath))
        {
            var fromProgramData = ParseFile(programDataPath);
            if (fromProgramData.Count > 0)
                return fromProgramData;
        }

        if (!string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath))
        {
            var fromBundled = ParseFile(bundledPath);
            if (fromBundled.Count > 0)
                return fromBundled;
        }

        return DefaultLines();
    }

    public static IReadOnlyList<string> ParseContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<string>();

        var lines = new List<string>();
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith('#'))
                continue;

            var trimmed = raw.TrimEnd();
            if (trimmed.Length == 0)
                continue;

            lines.Add(trimmed.Length > MaxLineWidth ? trimmed[..MaxLineWidth] : trimmed);
        }

        return lines;
    }

    private static List<string> ParseFile(string path)
    {
        try
        {
            return ParseContent(File.ReadAllText(path)).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<string> DefaultLines() =>
    [
        " _     ___    ____ ",
        "| |   / _ \\  / ___|",
        "| |  | | | | \\___ \\",
        "| |  | |_| |  ___) |",
        "|_|   \\___/  |____/ ",
    ];
}
