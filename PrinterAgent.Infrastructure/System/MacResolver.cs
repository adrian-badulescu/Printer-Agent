using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using PrinterAgent.Application.Interfaces;

namespace PrinterAgent.Infrastructure.System;

public class MacResolver : IMacResolver
{
    public string GetMacAddress()
    {
        var macAddress = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(nic => nic.GetPhysicalAddress().ToString())
            .FirstOrDefault();

        if (string.IsNullOrEmpty(macAddress))
        {
            macAddress = "UNKNOWN-MAC";
        }

        // Return SHA256 hashed MAC as requested
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(macAddress));
        return Convert.ToHexString(hashBytes);
    }
}
