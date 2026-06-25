using PrinterAgent.Application.Storage;

namespace PrinterAgent.Infrastructure.Printing;

/// <summary>
/// Loads receipt header text from ProgramData (operator-editable) or bundled fallback.
/// </summary>
public static class ReceiptHeaderAsciiReader
{
    public const string FileName = "receipt-header.ascii";
    public const int MaxLineWidth = 32;
    public const string DefaultHeaderText = "Universal Restaurant Systems";
    public const string DefaultUrlText = "www.universalrestaurant.systems";

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

            foreach (var wrapped in SplitForReceiptWidth(trimmed))
                lines.Add(wrapped);
        }

        return lines;
    }

    internal static IReadOnlyList<string> SplitForReceiptWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        if (text.Length <= MaxLineWidth)
            return [text];

        var searchEnd = Math.Min(MaxLineWidth, text.Length - 1);
        var dot = text.LastIndexOf('.', searchEnd);
        if (dot > 0 && dot < text.Length - 1)
            return [text[..(dot + 1)], text[(dot + 1)..]];

        return [text[..MaxLineWidth], text[MaxLineWidth..]];
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

    public static IReadOnlyList<string> DefaultLines()
    {
        var lines = new List<string> { DefaultHeaderText };
        lines.AddRange(SplitForReceiptWidth(DefaultUrlText));
        return lines;
    }
}
