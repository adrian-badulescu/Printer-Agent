namespace PrinterAgent.Application.Storage;

/// <summary>
/// Atomic writes to ProgramData JSON with retries when the Windows service briefly locks the file.
/// </summary>
public static class AgentProgramDataJsonWriter
{
    private const int MaxAttempts = 8;

    public static void WriteAtomic(string path, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(json);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";

        IOException? lastIo = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                File.WriteAllText(temp, json);
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);

                return;
            }
            catch (IOException ex) when (attempt < MaxAttempts && IsTransientFileLock(ex))
            {
                lastIo = ex;
                CleanupTemp(temp);
                Thread.Sleep(100 * attempt);
            }
            catch (UnauthorizedAccessException ex)
            {
                CleanupTemp(temp);
                throw new IOException(
                    $"Cannot write to {path}. Re-run the installer as Administrator, or execute scripts\\Setup-ProgramData.ps1 elevated, then retry.",
                    ex);
            }
            catch
            {
                CleanupTemp(temp);
                throw;
            }
        }

        CleanupTemp(temp);
        throw new IOException(
            $"Could not replace {path} after {MaxAttempts} attempts. The Printer Agent service may be reloading agent.json — wait a few seconds and click Save again.",
            lastIo);
    }

    private static bool IsTransientFileLock(IOException ex)
    {
        var win32 = ex.HResult & 0xFFFF;
        if (win32 is 0x20 or 0x21)
            return true;

        return ex.Message.Contains("remove the file to be replaced", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupTemp(string temp)
    {
        if (!File.Exists(temp))
            return;

        try
        {
            File.Delete(temp);
        }
        catch
        {
            // ignore
        }
    }
}
