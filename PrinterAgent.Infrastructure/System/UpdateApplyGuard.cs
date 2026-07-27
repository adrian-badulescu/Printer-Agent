using System.Text.Json;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Infrastructure.System;

/// <summary>Prevents update check storms and overlapping silent installs.</summary>
internal static class UpdateApplyGuard
{
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan InProgressTtl = TimeSpan.FromMinutes(20);

    internal static string UpdatesDirectory => UpdateInstallerLauncher.GetUpdatesDirectory();

    internal static string StateFilePath => Path.Combine(UpdatesDirectory, "update-state.json");

    internal static string InProgressLockPath => Path.Combine(UpdatesDirectory, ".update-in-progress");

    internal static bool ShouldSkipApply(out string? reason)
    {
        reason = null;

        if (TryReadInProgressLock(out var lockUtc) && DateTime.UtcNow - lockUtc < InProgressTtl)
        {
            reason = $"update in progress since {lockUtc:O}";
            return true;
        }

        if (!File.Exists(StateFilePath))
            return false;

        try
        {
            var json = File.ReadAllText(StateFilePath);
            var state = JsonSerializer.Deserialize<UpdateState>(json);
            if (state?.LastFailureUtc is null)
                return false;

            var elapsed = DateTime.UtcNow - state.LastFailureUtc.Value;
            if (elapsed < FailureCooldown)
            {
                reason = $"last failure {elapsed.TotalMinutes:F0} min ago (cooldown {FailureCooldown.TotalMinutes:F0} min)";
                return true;
            }
        }
        catch
        {
            // ignore corrupt state file
        }

        return false;
    }

    internal static void MarkApplyStarting()
    {
        Directory.CreateDirectory(UpdatesDirectory);
        File.WriteAllText(InProgressLockPath, DateTime.UtcNow.ToString("O"));
    }

    internal static void MarkApplySucceeded()
    {
        TryDelete(InProgressLockPath);
        WriteState(new UpdateState { LastSuccessUtc = DateTime.UtcNow });
    }

    internal static void MarkApplyFailed()
    {
        TryDelete(InProgressLockPath);
        WriteState(new UpdateState { LastFailureUtc = DateTime.UtcNow });
    }

    private static bool TryReadInProgressLock(out DateTime lockUtc)
    {
        lockUtc = default;
        if (!File.Exists(InProgressLockPath))
            return false;

        var text = File.ReadAllText(InProgressLockPath).Trim();
        return DateTime.TryParse(text, null, global::System.Globalization.DateTimeStyles.RoundtripKind, out lockUtc);
    }

    private static void WriteState(UpdateState state)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var json = JsonSerializer.Serialize(state);
        File.WriteAllText(StateFilePath, json);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private sealed class UpdateState
    {
        public DateTime? LastFailureUtc { get; init; }
        public DateTime? LastSuccessUtc { get; init; }
    }
}
