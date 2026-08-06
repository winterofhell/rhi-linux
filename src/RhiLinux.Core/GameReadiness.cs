using System.Text.Json.Serialization;

namespace RhiLinux.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameReadinessState
{
    Ready,
    ReadyWithWarnings,
    NeedsConfiguration,
    NeedsUserSelection,
    Unsupported,
    Unavailable,
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReadinessIssueSeverity
{
    Information,
    Warning,
    Blocking
}

public static class ReadinessIssueCodes
{
    public const string InstallRootMissing = "InstallRootMissing";
    public const string InstallRootUnreadable = "InstallRootUnreadable";
    public const string ExecutableMissing = "ExecutableMissing";
    public const string ExecutableInvalid = "ExecutableInvalid";
    public const string ExecutableAmbiguous = "ExecutableAmbiguous";
    public const string UnsupportedArchitecture = "UnsupportedArchitecture";
    public const string NativeGameUnsupported = "NativeGameUnsupported";
    public const string DeploymentDirectoryMissing = "DeploymentDirectoryMissing";
    public const string DeploymentDirectoryUnsafe = "DeploymentDirectoryUnsafe";
    public const string ForeignProxyConflict = "ForeignProxyConflict";
    public const string InterruptedDeployment = "InterruptedDeployment";
    public const string RecoveryRequired = "RecoveryRequired";
    public const string AntiCheatDetected = "AntiCheatDetected";
    public const string LauncherConfigurationRequired = "LauncherConfigurationRequired";
    public const string PrefixMissing = "PrefixMissing";
    public const string SourceMetadataStale = "SourceMetadataStale";
    public const string ArtifactUnavailable = "ArtifactUnavailable";
    public const string ArtifactUpdateAvailable = "ArtifactUpdateAvailable";
    public const string InstalledComponentConflict = "InstalledComponentConflict";
    public const string ManualOverrideInvalid = "ManualOverrideInvalid";
    public const string InsufficientPermissions = "InsufficientPermissions";
    public const string FilesystemUnsupported = "FilesystemUnsupported";
    public const string PlannerFailed = "PlannerFailed";
    public const string EvaluationFailed = "EvaluationFailed";
}

public sealed record GameReadinessIssue(
    string Code,
    ReadinessIssueSeverity Severity,
    string Title,
    string Message,
    string? ActionId = null,
    IReadOnlyDictionary<string, string>? Context = null)
{
    public IReadOnlyDictionary<string, string> EffectiveContext { get; } =
        Context ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record DeploymentPlanSummary(
    int OperationsCount,
    int FilesAdded,
    int FilesReplaced,
    int FilesRenamed,
    int BackupsCreated,
    int ConfigurationChanges,
    bool ManualLauncherStepRequired,
    bool RollbackAvailable,
    bool RequiresConfirmation,
    IReadOnlyList<string> Highlights)
{
    public static DeploymentPlanSummary From(DeploymentPlan plan)
    {
        var operations = plan.Operations;
        var filesAdded = operations.Count(op =>
            (op.Type is DeploymentOperationType.Copy or DeploymentOperationType.ExtractArchive) &&
            string.IsNullOrWhiteSpace(op.BackupPath));
        var filesReplaced = plan.FileDecisions.Count(decision => decision.ReplacesManagedFile) +
            operations.Count(op => op.Type == DeploymentOperationType.Copy && !string.IsNullOrWhiteSpace(op.BackupPath));
        var filesRenamed = operations.Count(op => op.Type == DeploymentOperationType.Move);
        var backups = operations.Count(op =>
            op.Type == DeploymentOperationType.Backup || !string.IsNullOrWhiteSpace(op.BackupPath));
        var configChanges = operations.Count(op =>
            op.Type is DeploymentOperationType.WriteIniValue or DeploymentOperationType.ClearConfigurationPatches);
        var highlights = new List<string>();
        if (filesAdded > 0) highlights.Add($"Add: {filesAdded} files");
        if (filesReplaced > 0) highlights.Add($"Replace: {filesReplaced} managed file{(filesReplaced == 1 ? "" : "s")}");
        if (filesRenamed > 0) highlights.Add($"Rename: {filesRenamed} managed file{(filesRenamed == 1 ? "" : "s")}");
        if (backups > 0) highlights.Add($"Back up: {backups} files");
        if (configChanges > 0) highlights.Add($"Modify configuration: {configChanges} file{(configChanges == 1 ? "" : "s")}");
        if (!string.IsNullOrWhiteSpace(plan.LaunchOption))
            highlights.Add("Launcher configuration: manual copy required");
        highlights.Add("Rollback available: yes");
        if (plan.RequiresConfirmation) highlights.Add("Explicit confirmation required");
        return new(
            operations.Count,
            filesAdded,
            filesReplaced,
            filesRenamed,
            backups,
            configChanges,
            !string.IsNullOrWhiteSpace(plan.LaunchOption),
            true,
            plan.RequiresConfirmation,
            highlights);
    }
}

public sealed record LaunchConfigurationResult(
    GameLauncher Launcher,
    string DisplayValue,
    string CopyValue,
    string PlacementInstructions,
    LaunchOptionStatus Status,
    string Explanation,
    bool ManualCopyRequired,
    bool AutomaticEditingSupported);

public sealed record RecommendedSetupOption(
    string Id,
    string Title,
    string Explanation,
    bool IsSupported,
    bool IsPrimary,
    string? UnsupportedReason = null);

public sealed record RecommendedSetupResult(
    string Summary,
    IReadOnlyList<RecommendedSetupOption> Options,
    string? PrimaryOptionId);

public sealed record GameReadinessResult(
    GameInstallId InstallId,
    GameReadinessState State,
    string Summary,
    string NextAction,
    DeploymentTarget? Target,
    IReadOnlyList<ComponentStatus> Components,
    IReadOnlyList<GameReadinessIssue> Issues,
    LaunchConfigurationResult? LaunchConfiguration,
    DeploymentPlanSummary? RecommendedPlan,
    RecommendedSetupResult? RecommendedSetup,
    DateTimeOffset EvaluatedAt,
    string EvaluationFingerprint,
    bool FromCache = false)
{
    public int BlockingIssueCount => Issues.Count(issue => issue.Severity == ReadinessIssueSeverity.Blocking);
    public int WarningIssueCount => Issues.Count(issue => issue.Severity == ReadinessIssueSeverity.Warning);
    public bool HasBlockingIssues => BlockingIssueCount > 0;
}

public interface ILaunchConfigurationProvider
{
    Task<LaunchConfigurationResult> GetAsync(
        InstalledGame game,
        string? requiredProxyDll,
        string? requiredLaunchOption,
        CancellationToken cancellationToken = default);
}

public interface IGameReadinessService
{
    Task<GameReadinessResult> EvaluateAsync(
        InstalledGame game,
        GameReadinessEvaluationOptions? options = null,
        CancellationToken cancellationToken = default);

    void Invalidate(GameInstallId installId);
    void InvalidateAll();
}

public sealed record GameReadinessEvaluationOptions(
    bool AllowNetwork = false,
    bool ForceRefresh = false,
    bool BuildRecommendedPlan = true,
    bool WarnBeforeAntiCheatDeployments = true,
    bool PreferExistingManagedVersions = true,
    IReadOnlyList<ComponentStatus>? PrefetchedComponents = null,
    string? PrefetchedProxyName = null,
    string? PrefetchedLaunchOption = null,
    LaunchConfigurationResult? PrefetchedLaunchConfiguration = null,
    string? ArtifactMetadataVersion = null,
    GameOverride? ManualOverride = null);
