using System.Security.Cryptography;
using System.Text;
using PrinterAgent.Application.Interfaces;

namespace PrinterAgent.Infrastructure.LocalApi;

public sealed class LocalPrintAuthTokenProvider : ILocalPrintAuthTokenProvider
{
    private readonly IDeviceCredentialStore _deviceCredentialStore;

    public LocalPrintAuthTokenProvider(IDeviceCredentialStore deviceCredentialStore)
    {
        _deviceCredentialStore = deviceCredentialStore;
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        await _deviceCredentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var credential = _deviceCredentialStore.DeviceCredential;
        if (string.IsNullOrWhiteSpace(credential))
            return null;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credential.Trim()));
        return Convert.ToHexString(hash)[..32];
    }
}
