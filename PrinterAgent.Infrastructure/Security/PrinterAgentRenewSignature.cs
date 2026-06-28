using System.Security.Cryptography;
using System.Text;

namespace PrinterAgent.Infrastructure.Security;

public static class PrinterAgentRenewSignature
{
    public static string BuildPayload(string agentId, Guid clientInstanceId, DateTime timestampUtc)
    {
        var ts = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : timestampUtc.ToUniversalTime();
        return $"{agentId}|{clientInstanceId:D}|{ts:O}";
    }

    public static string Compute(string deviceCredential, string agentId, Guid clientInstanceId, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(deviceCredential))
            return string.Empty;

        var payload = BuildPayload(agentId, clientInstanceId, timestampUtc);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(deviceCredential));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
