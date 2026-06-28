using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;
using PrinterAgent.Infrastructure.Security;

namespace PrinterAgent.Infrastructure.Persistence;

public sealed class DeviceCredentialStore : IDeviceCredentialStore
{
    private const string CredentialFileName = "device.credential.json";

    private static readonly SemaphoreSlim SaveLock = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<DeviceCredentialStore> _logger;
    private readonly string _baseDir;
    private string? _agentId;
    private string? _deviceCredential;

    public DeviceCredentialStore(ILogger<DeviceCredentialStore> logger)
    {
        _logger = logger;
        _baseDir = AgentProgramData.Root;
    }

    public string? AgentId => _agentId;

    public string? DeviceCredential => _deviceCredential;

    public bool HasCredential =>
        !string.IsNullOrWhiteSpace(_agentId) && !string.IsNullOrWhiteSpace(_deviceCredential);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_baseDir, CredentialFileName);
        if (!File.Exists(path))
        {
            _agentId = null;
            _deviceCredential = null;
            return;
        }

        try
        {
            await using var fs = File.OpenRead(path);
            var fileDto = await JsonSerializer.DeserializeAsync<DeviceCredentialFileDto>(fs, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            if (fileDto == null || string.IsNullOrWhiteSpace(fileDto.AgentId))
            {
                _agentId = null;
                _deviceCredential = null;
                return;
            }

            var credential = ResolveCredential(fileDto);
            if (string.IsNullOrWhiteSpace(credential))
            {
                _agentId = null;
                _deviceCredential = null;
                return;
            }

            _agentId = fileDto.AgentId;
            _deviceCredential = credential;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot read {File}; device credential ignored.", CredentialFileName);
            _agentId = null;
            _deviceCredential = null;
        }
    }

    public async Task SaveAsync(string agentId, string deviceCredential, CancellationToken cancellationToken = default)
    {
        _agentId = agentId;
        _deviceCredential = deviceCredential;

        DeviceCredentialFileDto fileDto;
        if (SessionAccessTokenProtector.IsSupported)
        {
            fileDto = new DeviceCredentialFileDto
            {
                AgentId = agentId,
                Credential = null,
                CredentialProtected = SessionAccessTokenProtector.ProtectToBase64(deviceCredential)
            };
        }
        else
        {
            fileDto = new DeviceCredentialFileDto
            {
                AgentId = agentId,
                Credential = deviceCredential,
                CredentialProtected = null
            };
        }

        var path = Path.Combine(_baseDir, CredentialFileName);
        Directory.CreateDirectory(_baseDir);

        await SaveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tempPath = Path.Combine(_baseDir, CredentialFileName + ".tmp." + Guid.NewGuid().ToString("N"));
            await using (var fs = new FileStream(
                               tempPath,
                               FileMode.Create,
                               FileAccess.Write,
                               FileShare.Read,
                               bufferSize: 4096,
                               options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(fs, fileDto, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
            TryDeleteQuiet(tempPath);
        }
        finally
        {
            SaveLock.Release();
        }

        _logger.LogInformation("Device credential saved to {File} (agentId={AgentId}).", CredentialFileName, agentId);
    }

    private static string? ResolveCredential(DeviceCredentialFileDto fileDto)
    {
        if (!string.IsNullOrWhiteSpace(fileDto.Credential))
            return fileDto.Credential;
        if (string.IsNullOrWhiteSpace(fileDto.CredentialProtected))
            return null;
        if (!SessionAccessTokenProtector.IsSupported)
            return null;

        try
        {
            return SessionAccessTokenProtector.UnprotectFromBase64(fileDto.CredentialProtected);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private sealed class DeviceCredentialFileDto
    {
        public string AgentId { get; set; } = string.Empty;
        public string? Credential { get; set; }
        public string? CredentialProtected { get; set; }
    }
}
