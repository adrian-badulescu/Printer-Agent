using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAgent.Infrastructure.Persistence;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class AgentProgramDataAgentJsonSyncTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _installDirJson;
    private readonly string _programDataJson;

    public AgentProgramDataAgentJsonSyncTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "urs-agent-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _installDirJson = Path.Combine(_tempRoot, "install-agent.json");
        _programDataJson = Path.Combine(_tempRoot, "agent.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void TryWriteVersionFromInstallDir_updates_stale_programdata_version()
    {
        File.WriteAllText(_installDirJson, """{"Version":"1.5.10"}""");
        File.WriteAllText(_programDataJson, """{"Version":"1.5.7","RestaurantId":"r1"}""");

        AgentProgramDataAgentJsonSync.TryWriteVersion("1.5.10", _programDataJson, NullLogger.Instance);

        var root = JsonNode.Parse(File.ReadAllText(_programDataJson))!.AsObject();
        Assert.Equal("1.5.10", root["Version"]!.GetValue<string>());
        Assert.Equal("r1", root["RestaurantId"]!.GetValue<string>());
    }

    [Fact]
    public void TryWriteVersion_noop_when_already_aligned()
    {
        var before = """{"Version":"1.5.9"}""";
        File.WriteAllText(_programDataJson, before);

        AgentProgramDataAgentSyncTryWriteVersion("1.5.9", _programDataJson);

        Assert.Equal(before, File.ReadAllText(_programDataJson));
    }

    private static void AgentProgramDataAgentSyncTryWriteVersion(string version, string path) =>
        AgentProgramDataAgentJsonSync.TryWriteVersion(version, path, NullLogger.Instance);
}
