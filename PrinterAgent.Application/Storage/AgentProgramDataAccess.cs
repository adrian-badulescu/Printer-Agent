using System.Security.AccessControl;
using System.Security.Principal;

namespace PrinterAgent.Application.Storage;

/// <summary>
/// Ensures interactive users and services can read/write under %ProgramData%\URSPrinterAgent.
/// </summary>
public static class AgentProgramDataAccess
{
    public static void EnsureWritable()
    {
        var root = AgentProgramData.Root;
        Directory.CreateDirectory(root);

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var dirInfo = new DirectoryInfo(root);
            var security = dirInfo.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify | FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dirInfo.SetAccessControl(security);
        }
        catch
        {
            // MSI / Setup-ProgramData.ps1 may already have set ACLs; do not block startup.
        }
    }
}
