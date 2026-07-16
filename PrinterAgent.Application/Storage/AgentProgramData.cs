namespace PrinterAgent.Application.Storage;

/// <summary>
/// Machine-wide data directory for the printer agent (same path in dev and production).
/// Windows: <c>%ProgramData%\URSPrinterAgent</c> (e.g. <c>C:\ProgramData\URSPrinterAgent</c>).
/// </summary>
public static class AgentProgramData
{
    public const string FolderName = "URSPrinterAgent";

    public const string ReceiptHeaderFileName = "receipt-header.ascii";

    private static readonly string RootInitialized = InitializeRoot();

    public static string Root => RootInitialized;

    private static string InitializeRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var path = Path.Combine(root, FolderName);
        Directory.CreateDirectory(path);
        AgentProgramDataAccess.EnsureWritable();
        return path;
    }
}
