using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RhiLinux.Core;

public enum OperationPreflightSeverity
{
    Information,
    Warning,
    Blocking
}

public enum RunningGameState
{
    NotDetected,
    PossiblyRunning,
    Running,
    Unknown
}

public static class OperationPreflightCodes
{
    public const string TargetUnavailable = "TargetUnavailable";
    public const string TargetNotWritable = "TargetNotWritable";
    public const string StateNotWritable = "StateNotWritable";
    public const string BackupNotWritable = "BackupNotWritable";
    public const string InsufficientSpace = "InsufficientSpace";
    public const string LowSpace = "LowSpace";
    public const string UnsafeSymlink = "UnsafeSymlink";
    public const string PlanChanged = "PlanChanged";
    public const string GameRunning = "GameRunning";
    public const string GamePossiblyRunning = "GamePossiblyRunning";
}

public sealed record OperationPreflightIssue(
    string Code,
    OperationPreflightSeverity Severity,
    string Title,
    string Message,
    string? TechnicalDetail = null);

public sealed record RunningGameResult(
    RunningGameState State,
    IReadOnlyList<int> ProcessIds,
    string Summary);

public sealed record OperationPreflightResult(
    IReadOnlyList<OperationPreflightIssue> Issues,
    RunningGameResult RunningGame,
    long RequiredBytes,
    long AvailableBytes,
    DateTimeOffset CheckedAt)
{
    public bool CanProceed => Issues.All(issue => issue.Severity != OperationPreflightSeverity.Blocking);
    public bool RequiresWarningConfirmation => Issues.Any(issue => issue.Severity == OperationPreflightSeverity.Warning);
}

public sealed record PlanFilePrecondition(
    string Path,
    bool Exists,
    long Length,
    string? Sha256,
    long? LastWriteTimeUtcTicks = null);

public sealed record DeploymentPlanPreconditions(
    GameInstallId InstallId,
    string DeploymentDirectory,
    bool DeploymentDirectoryExists,
    bool RecoveryPending,
    IReadOnlyList<PlanFilePrecondition> CriticalFiles);

public interface IRunningGameDetector
{
    Task<RunningGameResult> DetectAsync(DeploymentPlan plan, CancellationToken cancellationToken = default);
}

public interface IOperationPreflightService
{
    Task<OperationPreflightResult> CheckAsync(
        DeploymentPlan plan,
        CancellationToken cancellationToken = default);
}

public sealed class LinuxRunningGameDetector : IRunningGameDetector
{
    public Task<RunningGameResult> DetectAsync(
        DeploymentPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            return Task.FromResult(new RunningGameResult(RunningGameState.Unknown, [],
                "Running-game detection is unavailable on this platform."));
        var executable = plan.Operations.Select(operation => operation.Target)
            .FirstOrDefault(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        executable ??= FindExecutableFromDeployment(plan);
        if (executable is null)
            return Task.FromResult(new RunningGameResult(RunningGameState.Unknown, [],
                "No executable was available for process matching."));

        var fullPath = Path.GetFullPath(executable);
        var fileName = Path.GetFileName(executable);
        var exact = new List<int>();
        var possible = new List<int>();
        try
        {
            foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!int.TryParse(Path.GetFileName(processDirectory), out var processId)) continue;
                string commandLine;
                try
                {
                    var bytes = File.ReadAllBytes(Path.Combine(processDirectory, "cmdline"));
                    commandLine = System.Text.Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (commandLine.Contains(fullPath, StringComparison.Ordinal))
                    exact.Add(processId);
                else if (commandLine.Contains(fileName, StringComparison.OrdinalIgnoreCase) &&
                         (commandLine.Contains("wine", StringComparison.OrdinalIgnoreCase) ||
                          commandLine.Contains("proton", StringComparison.OrdinalIgnoreCase)))
                    possible.Add(processId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new RunningGameResult(RunningGameState.Unknown, [],
                "Local process information could not be read."));
        }

        if (exact.Count > 0)
            return Task.FromResult(new RunningGameResult(RunningGameState.Running, exact,
                $"The selected executable appears in {exact.Count} running process command line(s)."));
        if (possible.Count > 0)
            return Task.FromResult(new RunningGameResult(RunningGameState.PossiblyRunning, possible,
                $"A Wine or Proton process mentions {fileName}."));
        return Task.FromResult(new RunningGameResult(RunningGameState.NotDetected, [],
            "No matching Wine or Proton process was detected."));
    }

    private static string? FindExecutableFromDeployment(DeploymentPlan plan)
    {
        if (!Directory.Exists(plan.DeploymentDirectory)) return null;
        try
        {
            return Directory.EnumerateFiles(plan.DeploymentDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class OperationPreflightService : IOperationPreflightService
{
    private readonly IRunningGameDetector runningGameDetector;
    private readonly XdgPaths paths;

    public OperationPreflightService(
        XdgPaths? paths = null,
        IRunningGameDetector? runningGameDetector = null)
    {
        this.paths = paths ?? new XdgPaths();
        this.runningGameDetector = runningGameDetector ?? new LinuxRunningGameDetector();
    }

    public async Task<OperationPreflightResult> CheckAsync(
        DeploymentPlan plan,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<OperationPreflightIssue>();
        if (!Directory.Exists(plan.GameRoot) || !Directory.Exists(plan.DeploymentDirectory))
            issues.Add(Block(OperationPreflightCodes.TargetUnavailable, "Game files are unavailable",
                "The game installation or deployment directory is no longer available. Reconnect its disk and refresh the game."));
        else
        {
            if (!GameOverrideValidator.IsContained(plan.GameRoot, plan.DeploymentDirectory))
                issues.Add(Block(OperationPreflightCodes.TargetUnavailable, "Deployment target is unsafe",
                    "The deployment directory is outside the selected game installation."));
            if (!HasWritePermission(plan.DeploymentDirectory))
                issues.Add(Block(OperationPreflightCodes.TargetNotWritable, "Game directory is not writable",
                    "Grant the current user write access to the deployment directory before applying changes."));
            ValidateSymlinks(plan, issues);
        }

        if (!HasWritePermission(ExistingDirectory(paths.AppDataDirectory)))
            issues.Add(Block(OperationPreflightCodes.StateNotWritable, "Application state is not writable",
                "RHI cannot safely record ownership and transaction state in its data directory."));
        var backupDirectory = Path.Combine(plan.GameRoot, ".rhi-linux", "backups");
        if (!HasWritePermission(ExistingDirectory(backupDirectory)))
            issues.Add(Block(OperationPreflightCodes.BackupNotWritable, "Backup storage is not writable",
                "RHI cannot create rollback data for this operation."));

        if (plan.ApprovedPreconditions is not null)
        {
            var current = await DeploymentPlanApproval.CaptureAsync(plan, cancellationToken).ConfigureAwait(false);
            if (!SamePreconditions(plan.ApprovedPreconditions, current))
                issues.Add(Block(OperationPreflightCodes.PlanChanged, "Plan needs review",
                    "Game files changed after this plan was shown. Regenerate and review the plan before continuing."));
        }
        var required = EstimateRequiredBytes(plan);
        var available = AvailableBytes(plan.DeploymentDirectory);
        if (available >= 0 && available < required)
            issues.Add(Block(OperationPreflightCodes.InsufficientSpace, "Not enough free disk space",
                $"The operation needs about {FormatBytes(required)}, but only {FormatBytes(available)} is available."));
        else if (available >= 0 && available < required * 2)
            issues.Add(new(OperationPreflightCodes.LowSpace, OperationPreflightSeverity.Warning,
                "Disk space is low",
                $"The operation should fit, but only {FormatBytes(available)} is available for files, backups, and temporary data."));

        var running = await runningGameDetector.DetectAsync(plan, cancellationToken).ConfigureAwait(false);
        if (running.State == RunningGameState.Running)
            issues.Add(Block(OperationPreflightCodes.GameRunning, "Game is running",
                "Close the game before modifying its files.", running.Summary));
        else if (running.State == RunningGameState.PossiblyRunning)
            issues.Add(new(OperationPreflightCodes.GamePossiblyRunning, OperationPreflightSeverity.Warning,
                "Game may be running",
                "A related Wine or Proton process was found. Close the game, or confirm that it is safe to continue.",
                running.Summary));

        return new(issues, running, required, available, DateTimeOffset.UtcNow);
    }

    private static void ValidateSymlinks(DeploymentPlan plan, ICollection<OperationPreflightIssue> issues)
    {
        foreach (var target in plan.Operations.Select(operation => operation.Target).Distinct(StringComparer.Ordinal))
        {
            var parent = File.Exists(target) ? new FileInfo(target).Directory : new FileInfo(target).Directory;
            if (parent is null || !parent.Exists) continue;
            try
            {
                var resolved = parent.ResolveLinkTarget(true)?.FullName;
                if (resolved is not null && !GameOverrideValidator.IsContained(plan.GameRoot, resolved))
                {
                    issues.Add(Block(OperationPreflightCodes.UnsafeSymlink, "A target escapes the game directory",
                        "A symbolic link redirects a planned file operation outside the game installation.", target));
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }
    }

    private static bool SamePreconditions(DeploymentPlanPreconditions approved, DeploymentPlanPreconditions current) =>
        approved.InstallId.Equals(current.InstallId) &&
        Path.GetFullPath(approved.DeploymentDirectory).Equals(Path.GetFullPath(current.DeploymentDirectory), StringComparison.Ordinal) &&
        approved.DeploymentDirectoryExists == current.DeploymentDirectoryExists &&
        approved.RecoveryPending == current.RecoveryPending &&
        approved.CriticalFiles.SequenceEqual(current.CriticalFiles);

    private static long EstimateRequiredBytes(DeploymentPlan plan)
    {
        long sourceBytes = 0;
        long backupBytes = 0;
        foreach (var operation in plan.Operations)
        {
            if (operation.Source is { } source && File.Exists(source)) sourceBytes += new FileInfo(source).Length;
            if (operation.Type == DeploymentOperationType.Backup && File.Exists(operation.Target))
                backupBytes += new FileInfo(operation.Target).Length;
        }
        return Math.Max(16 * 1024 * 1024, checked(sourceBytes * 2 + backupBytes + 8 * 1024 * 1024));
    }

    private static long AvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    private static bool HasWritePermission(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return false;
        if (!OperatingSystem.IsLinux()) return true;
        try
        {
            return Access(directory, 2) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(string pathname, int mode);

    private static string? ExistingDirectory(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) return null;
            current = parent;
        }
        return current;
    }

    private static OperationPreflightIssue Block(string code, string title, string message, string? detail = null) =>
        new(code, OperationPreflightSeverity.Blocking, title, message, detail);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MiB",
        _ => $"{bytes / 1024d:0.##} KiB"
    };
}

public static class DeploymentPlanApproval
{
    public static async Task SealAsync(DeploymentPlan plan, CancellationToken cancellationToken = default)
    {
        plan.ApprovedPreconditions = await CaptureAsync(plan, cancellationToken).ConfigureAwait(false);
        plan.ApprovedAt = DateTimeOffset.UtcNow;
        plan.Fingerprint ??= DeploymentPlanFingerprint.Compute(plan);
    }

    public static async Task<DeploymentPlanPreconditions> CaptureAsync(
        DeploymentPlan plan,
        CancellationToken cancellationToken = default)
    {
        var criticalPaths = plan.Operations
            .Where(operation => operation.Type is DeploymentOperationType.Backup or DeploymentOperationType.Copy or
                DeploymentOperationType.Move or DeploymentOperationType.DeleteManagedFile or
                DeploymentOperationType.DeleteVerifiedFile or DeploymentOperationType.WriteIniValue or
                DeploymentOperationType.RestoreBackup or DeploymentOperationType.ForgetOwnership)
            .Select(operation => operation.Target)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var states = new List<PlanFilePrecondition>(criticalPaths.Length);
        foreach (var path in criticalPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                states.Add(new(path, false, 0, null));
                continue;
            }
            var info = new FileInfo(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            states.Add(new(path, true, info.Length, hash, info.LastWriteTimeUtc.Ticks));
        }
        var installId = GameInstallId.TryParse(plan.InstallId, out var parsed) ? parsed : new GameInstallId(plan.InstallId);
        return new(installId, plan.DeploymentDirectory, Directory.Exists(plan.DeploymentDirectory),
            DeploymentRecoveryProbe.Probe(plan.GameRoot).HasInterruptedTransaction, states);
    }
}

public static class DeploymentPlanFingerprint
{
    public static string Compute(DeploymentPlan plan)
    {
        var value = new StringBuilder()
            .Append(plan.InstallId).Append('\n')
            .Append(plan.GameRoot).Append('\n')
            .Append(plan.DeploymentDirectory).Append('\n')
            .Append(plan.Action).Append('\n');

        foreach (var operation in plan.Operations)
        {
            value.Append(operation.Type).Append('|')
                .Append(operation.Target).Append('|')
                .Append(operation.Source).Append('|')
                .Append(operation.Value).Append('|')
                .Append(operation.ExpectedSha256).Append('|')
                .Append(operation.Component).Append('|')
                .Append(operation.BackupPath).Append('|')
                .Append(operation.SourceUrl).Append('|')
                .Append(operation.SourceBlobSha256).Append('|')
                .Append(operation.SourceBundleSha256).Append('|')
                .Append(operation.BundleRelativePath).Append('|')
                .Append(operation.ConfigurationSchema).Append('|')
                .Append(operation.ConfigurationVersion).Append('|')
                .Append(operation.ClearConfigurationPatch).Append('|')
                .Append(operation.Purpose).Append('|')
                .Append(operation.Requirement).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())))
            .ToLowerInvariant();
    }
}
