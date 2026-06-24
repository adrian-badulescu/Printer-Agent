using System.Security.Cryptography;
using System.Text;

namespace PrinterAgent.Infrastructure.Security;

/// <summary>Windows DPAPI (LocalMachine) for Redis ACL password in ProgramData.</summary>
internal static class RedisCredentialProtector
{
    internal static bool IsSupported => OperatingSystem.IsWindows();

    internal static string ProtectToBase64(string password)
    {
        var plain = Encoding.UTF8.GetBytes(password);
        var blob = ProtectedData.Protect(plain, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(blob);
    }

    internal static string UnprotectFromBase64(string base64)
    {
        var blob = Convert.FromBase64String(base64);
        var plain = ProtectedData.Unprotect(blob, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plain);
    }
}
