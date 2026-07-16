using System.Security.AccessControl;
using System.Security.Principal;

namespace PrinterAgent.Application.Storage;

/// <summary>
/// Ensures interactive users and services can read/write under %ProgramData%\URSPrinterAgent.
/// </summary>
public static class AgentProgramDataAccess
{
    public static void EnsureWritable(string? programDataRoot = null)
    {
        var root = programDataRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonAppData))
                return;

            root = Path.Combine(commonAppData, AgentProgramData.FolderName);
        }

        Directory.CreateDirectory(root);

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var dirInfo = new DirectoryInfo(root);
            var security = dirInfo.GetAccessControl();
            AddModifyRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            AddModifyRule(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify | FileSystemRights.Read | FileSystemRights.Write);
            AddModifyRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            dirInfo.SetAccessControl(security);
        }
        catch
        {
            // MSI / Setup-ProgramData.ps1 may already have set ACLs; do not block startup.
        }
    }

    private static void AddModifyRule(
        DirectorySecurity security,
        WellKnownSidType sidType,
        FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sidType, null),
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
