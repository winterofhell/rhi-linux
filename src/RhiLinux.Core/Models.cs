using System.Text.Json.Serialization;

namespace RhiLinux.Core;

public enum PeArchitecture { Unknown, X86, X64, Arm64 }
public enum DetectionConfidence { None, Low, Medium, High }
public enum GameEngine { Unknown, Unreal, UnrealLegacy, Unity, ReEngine }
public enum ComponentKind { ReShade, RenoDx, OptiScaler, OptiPatcher }
public enum ManagedFileClass
{
    Unknown,
    ImmutableRuntimeBinary,
    MutableConfiguration,
    UserEditableConfiguration,
    ManagedGeneratedFile,
    Backup
}
public enum SteamInstallState { Installed, Installing }
public enum DeploymentFileRequirement
{
    Required,
    RequiredForSelectedMode,
    Conditional,
    Optional,
    Debug,
    Documentation,
    InstallerOnly,
    UninstallerMetadata,
    Unsupported,
    SuppliedByGame,
    NeverReplace,
    ObsoleteManaged,
    UnrelatedArchiveContent
}

public enum RemovalPathOwnership
{
    ManagedBySelectedComponent,
    SharedManagedDependency,
    ManagedByAnotherComponent,
    UserOwned,
    GameOwned,
    Unknown
}

public enum LaunchOptionFragmentOwnership
{
    PreExisting,
    UserManaged,
    RhiLinuxManaged
}

public enum InstallationOwnershipState
{
    Managed,
    Manual,
    PartiallyManaged,
    OwnershipUnknown
}

public sealed record ArtifactFingerprint(
    ComponentKind Component,
    PeArchitecture Architecture,
    string CanonicalPath,
    string Filename,
    string? Sha256,
    string? PeMetadata,
    string? DetectedVersion,
    string? SourceIdentity,
    bool Managed);

public enum DeploymentFileAction
{
    Deploy,
    ReplaceManaged,
    AdoptOfficial,
    PreserveExisting,
    Omit,
    RemoveManaged,
    MoveManaged
}
public enum ComponentHealth
{
    Unavailable,
    Available,
    Supported,
    DownloadRequired,
    Cached,
    Installed,
    Outdated,
    MissingDependency,
    ManifestUnavailable,
    Unsupported,
    Broken,
    Conflicting,
    PartiallyInstalled,
    IncorrectlyConfigured,
    RepairAvailable,
    ForeignInstallation,
    Experimental
}

public enum InstallationVerification
{
    None,
    Managed,
    RecognizedExisting,
    MetadataUnverified,
    OptionalCleanupAvailable,
    RepairNeeded,
    Broken
}

public enum LaunchOptionStatus
{
    NotRequired,
    NotDetected,
    Missing,
    NeedsUpdate,
    Correct
}

public readonly record struct OperationContext(
    GameInstallId InstallId,
    long SelectionGeneration,
    string GameRoot,
    string DeploymentDirectory,
    string? Executable,
    ComponentKind? Component = null)
{
    public static OperationContext From(DeploymentTarget game, long generation, ComponentKind? component = null) =>
        new(game.InstallId, generation, game.GameRoot, game.DeploymentDirectory, game.Executable, component);

    public static OperationContext From(InstalledGame game, long generation, ComponentKind? component = null) =>
        From(game.ToDeploymentTarget(), generation, component);

    public static OperationContext From(SteamGame game, long generation, ComponentKind? component = null) =>
        From(game.ToDeploymentTarget(), generation, component);

    public bool Matches(DeploymentTarget game, long generation) =>
        InstallId.Equals(game.InstallId) &&
        SelectionGeneration == generation &&
        PathsEqual(GameRoot, game.GameRoot) &&
        PathsEqual(DeploymentDirectory, game.DeploymentDirectory);

    public bool Matches(InstalledGame game, long generation) =>
        Matches(game.ToDeploymentTarget(), generation);

    public bool Matches(SteamGame game, long generation) =>
        Matches(game.ToDeploymentTarget(), generation);

    public bool MatchesPlan(DeploymentPlan plan) =>
        InstallId.Value == plan.InstallId &&
        SelectionGeneration == plan.SelectionGeneration &&
        PathsEqual(GameRoot, plan.GameRoot) &&
        PathsEqual(DeploymentDirectory, plan.DeploymentDirectory);

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.Ordinal);
}

public sealed record ExecutableCandidate(
    string Path,
    int Score,
    DetectionConfidence Confidence,
    PeArchitecture Architecture,
    long Size,
    IReadOnlyList<string> Reasons);

public sealed record SteamGame(
    uint AppId,
    string Name,
    string SteamRoot,
    string LibraryRoot,
    string GameRoot,
    string ProtonPrefix,
    string? Executable,
    string DeploymentDirectory,
    DetectionConfidence Confidence,
    string SelectionReason,
    GameEngine Engine,
    IReadOnlyList<ExecutableCandidate> Candidates,
    bool RequiresConfirmation = false,
    SteamInstallState InstallState = SteamInstallState.Installed,
    bool IsNativeLinux = false,
    bool HasProtonPrefix = true,
    string? InstallId = null,
    GameStore Store = GameStore.Steam,
    GameLauncher Launcher = GameLauncher.Steam,
    string? ExternalId = null,
    GameBinaryPlatform Platform = GameBinaryPlatform.Windows,
    CompatibilityEnvironment Environment = CompatibilityEnvironment.Proton,
    IReadOnlyList<SourceGameRecord>? Sources = null,
    IReadOnlyList<SourceDiagnostic>? SourceDiagnostics = null,
    bool IsActionable = true,
    string? UnsupportedReason = null)
{
    public string EffectiveInstallId =>
        InstallId
        ?? (Store == GameStore.Steam && AppId != 0
            ? GameInstallId.FromSteam(AppId, GameRoot, Executable).Value
            : GameInstallId.Create(Store, Launcher, ExternalId, GameRoot, Executable).Value);

    public uint? SteamAppId => Store == GameStore.Steam && AppId != 0 ? AppId : null;

    public string StoreBadge => Store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic",
        GameStore.Gog => "GOG",
        GameStore.Amazon => "Amazon",
        GameStore.Other => "Other",
        _ => "Unknown"
    };

    public string LauncherBadge => Launcher.ToString();

    public string IdentitySummary
    {
        get
        {
            var parts = new List<string> { StoreBadge, LauncherBadge };
            if (Engine != GameEngine.Unknown) parts.Add(Engine.ToString());
            if (IsNativeLinux || Platform == GameBinaryPlatform.Linux)
                parts.Add("Native");
            else if (!IsActionable && UnsupportedReason is not null)
                parts.Add("Unsupported");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record SteamManifestDiagnostic(
    string ManifestPath,
    uint? AppId,
    string? Name,
    string? InstallDirectory,
    string? StateFlags,
    string LibraryRoot,
    string Disposition,
    string Reason);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SteamRootSource
{
    Explicit,
    Environment,
    Native,
    Xdg,
    Flatpak,
    Snap,
    SteamLibrary
}

public sealed record SteamRootDiagnostic(
    string OriginalPath,
    string CanonicalPath,
    SteamRootSource Source,
    bool Exists,
    bool Readable,
    bool Deduplicated,
    int ManifestsFound,
    int GamesIncluded,
    string? SkipReason);

public sealed record ScanResult(
    IReadOnlyList<InstalledGame> Games,
    IReadOnlyList<string> SteamRoots,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SteamManifestDiagnostic>? ManifestDiagnostics = null,
    IReadOnlyList<SteamRootDiagnostic>? RootDiagnostics = null);

public enum ComponentLifecycleState
{
    Checking,
    NotInstalled,
    InstalledHealthy,
    InstalledWithWarnings,
    InstalledUnmanaged,
    InstalledMetadataIncomplete,
    UpdateAvailable,
    RepairRecommended,
    RepairRequired,
    Conflict,
    Unsupported,
    Unknown
}

public enum StackLayoutKind
{
    Empty,
    ReShadeOnly,
    ReShadeRenoDx,
    OptiScalerOnly,
    OptiScalerReShade,
    OptiScalerRenoDx,
    FullStack,
    Invalid
}

public enum RuntimeHealth
{
    Unknown,
    Healthy,
    Degraded,
    Broken
}

public enum FileIntegrityStatus
{
    Unknown,
    Complete,
    Incomplete,
    Corrupt,
    WrongArchitecture
}

public enum ConfigurationHealth
{
    Unknown,
    Valid,
    ValidWithUserChanges,
    RecoverableIssues,
    Invalid
}

public enum OwnershipHealth
{
    Unknown,
    Managed,
    Incomplete,
    Migrated,
    Unmanaged,
    Foreign,
    Unavailable
}

public enum UpdateAvailability
{
    Unknown,
    UpToDate,
    UpdateAvailable,
    StatusUnavailable,
    InstalledVersionUnknown,
    ManualInstallationDetected,
    NotInstalled
}

public enum CompatibilityStatus
{
    Unknown,
    Compatible,
    Experimental,
    Unsupported,
    Conflict
}

public sealed record ComponentStateEvidence(
    IReadOnlyList<string> DetectedFiles,
    IReadOnlyList<string> ExpectedFiles,
    IReadOnlyList<string> OptionalFiles,
    IReadOnlyList<string> MissingRequiredFiles,
    string? ActiveProxy,
    ComponentKind? ProxyOwner,
    PeArchitecture Architecture,
    string? InstalledArtifactIdentity,
    string? AvailableArtifactIdentity,
    string? RepairReason,
    string ReasonCode);

public sealed record ComponentStateReport(
    ComponentKind Component,
    ComponentLifecycleState State,
    RuntimeHealth Runtime,
    FileIntegrityStatus FileIntegrity,
    ConfigurationHealth Configuration,
    OwnershipHealth Ownership,
    UpdateAvailability Update,
    CompatibilityStatus Compatibility,
    string? Version,
    string Explanation,
    string? Diagnostic,
    ComponentStateEvidence Evidence);

public sealed record StackSnapshot(
    string InstallId,
    string GameRoot,
    string DeploymentDirectory,
    string? Executable,
    long Generation,
    StackLayoutKind Layout,
    string? ActiveProxy,
    ComponentKind? ProxyOwner,
    string? ReShadeRuntimePath,
    string? RenoDxAddonPath,
    string? OptiScalerRuntimePath,
    bool ChainingConfigured,
    PeArchitecture Architecture,
    OwnershipHealth Ownership,
    IReadOnlyList<ComponentStateReport> Components,
    string Summary,
    string? LaunchOptionRequirement = null,
    string? ChainMode = null,
    ArtifactFingerprint? ReShadeArtifactFingerprint = null,
    ArtifactFingerprint? RenoDxArtifactFingerprint = null,
    ArtifactFingerprint? OptiScalerArtifactFingerprint = null,
    IReadOnlyList<string>? RequiredConfigurationKeys = null,
    IReadOnlyList<string>? ConcreteDefects = null,
    IReadOnlyList<string>? Warnings = null,
    uint? SteamAppId = null)
{
    public bool IsHealthy =>
        ConcreteDefects is not { Count: > 0 } &&
        Components.All(component => component.State is not (
            ComponentLifecycleState.RepairRequired or ComponentLifecycleState.Conflict or
            ComponentLifecycleState.Unknown));

    public ComponentStateReport? ReportFor(ComponentKind component) =>
        Components.FirstOrDefault(x => x.Component == component);
}

public sealed record ComponentStatus(
    ComponentKind Component,
    ComponentHealth Health,
    string? Version,
    IReadOnlyList<string> Files,
    string Explanation,
    InstallationVerification Verification = InstallationVerification.None,
    string? Diagnostic = null,
    ComponentLifecycleState Lifecycle = ComponentLifecycleState.Unknown,
    string? RepairReason = null,
    string? ReasonCode = null,
    RuntimeHealth Runtime = RuntimeHealth.Unknown,
    OwnershipHealth Ownership = OwnershipHealth.Unknown,
    UpdateAvailability Update = UpdateAvailability.Unknown);

public sealed class ApplicationState
{
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public const int CurrentSchemaVersion = 2;
    public DateTimeOffset LastScanUtc { get; set; }
    public List<PersistedGameEntry> DiscoveredGames { get; set; } = [];
    public Dictionary<string, GameOverride> Overrides { get; set; } = new(StringComparer.Ordinal);
    public List<TransactionRecord> Transactions { get; set; } = [];
    public Dictionary<string, List<string>> ArtifactReferencesByInstallId { get; set; } = new(StringComparer.Ordinal);
    public List<OrphanedLegacyStateEntry> OrphanedLegacyEntries { get; set; } = [];
    public List<string> MigrationDiagnostics { get; set; } = [];
}

public sealed record PersistedGameEntry(
    string InstallId,
    string Name,
    GameStore Store,
    GameLauncher Launcher,
    string? ExternalId,
    uint? SteamAppId,
    string InstallRoot,
    string? Executable,
    string? DeploymentDirectory,
    string? Prefix,
    GameBinaryPlatform Platform = GameBinaryPlatform.Windows,
    CompatibilityEnvironment Environment = CompatibilityEnvironment.Proton,
    DetectionConfidence Confidence = DetectionConfidence.None,
    GameEngine Engine = GameEngine.Unknown,
    string SelectionReason = "",
    bool RequiresConfirmation = false,
    bool IsActionable = true,
    string? UnsupportedReason = null,
    bool IsStale = false,
    string? SteamRoot = null,
    string? LibraryRoot = null)
{
    public static PersistedGameEntry FromSteamGame(SteamGame game) => new(
        game.EffectiveInstallId,
        game.Name,
        game.Store,
        game.Launcher,
        game.ExternalId ?? game.SteamAppId?.ToString(),
        game.SteamAppId,
        game.GameRoot,
        game.Executable,
        game.DeploymentDirectory,
        string.IsNullOrWhiteSpace(game.ProtonPrefix) ? null : game.ProtonPrefix,
        game.Platform,
        game.Environment,
        game.Confidence,
        game.Engine,
        game.SelectionReason,
        game.RequiresConfirmation,
        game.IsActionable,
        game.UnsupportedReason,
        SteamRoot: game.SteamRoot,
        LibraryRoot: game.LibraryRoot);

    public static PersistedGameEntry FromInstalledGame(InstalledGame game) => new(
        game.InstallId.Value,
        game.Name,
        game.Store,
        game.PrimaryLauncher,
        game.ExternalId,
        game.SteamAppId,
        game.CanonicalInstallRoot,
        game.Executable,
        game.DeploymentDirectory,
        game.Prefix,
        game.Platform,
        game.Environment,
        game.Confidence,
        game.Engine,
        game.SelectionReason,
        game.RequiresConfirmation,
        game.IsActionable,
        game.UnsupportedReason,
        game.IsStale);

    [Obsolete("Use InstalledGame / DeploymentTarget. Steam-edge conversion only.")]
    public SteamGame ToSteamGame() => new(
        SteamAppId ?? 0,
        Name,
        SteamRoot ?? string.Empty,
        LibraryRoot ?? InstallRoot,
        InstallRoot,
        Prefix ?? string.Empty,
        Executable,
        DeploymentDirectory ?? InstallRoot,
        Confidence,
        SelectionReason,
        Engine,
        [],
        RequiresConfirmation,
        SteamInstallState.Installed,
        Platform == GameBinaryPlatform.Linux,
        !string.IsNullOrWhiteSpace(Prefix),
        InstallId,
        Store,
        Launcher,
        ExternalId,
        Platform,
        Environment,
        IsActionable: IsActionable,
        UnsupportedReason: UnsupportedReason);

    public DeploymentTarget ToDeploymentTarget() => new(
        new GameInstallId(InstallId),
        Name,
        InstallRoot,
        Executable,
        DeploymentDirectory ?? InstallRoot,
        Prefix,
        SteamAppId,
        Store,
        Launcher,
        ExternalId,
        Confidence,
        SelectionReason,
        Engine,
        [],
        RequiresConfirmation,
        Platform == GameBinaryPlatform.Linux,
        !string.IsNullOrWhiteSpace(Prefix),
        Platform,
        Environment,
        IsActionable,
        UnsupportedReason,
        IsStale);
}

public sealed record OrphanedLegacyStateEntry(
    string Kind,
    string Key,
    string Reason,
    string? PayloadJson = null);

public sealed record GameOverride(string? Executable, string? DeploymentDirectory);

public sealed record ManagedFile(
    string RelativePath,
    ComponentKind Component,
    string Sha256,
    string? Version,
    string? SourceUrl,
    string? BackupRelativePath,
    string? SourceBlobSha256 = null,
    string? SourceBundleSha256 = null,
    string? BundleRelativePath = null,
    string? BackupSha256 = null,
    ManagedFileClass FileClass = ManagedFileClass.Unknown,
    string? Purpose = null,
    DeploymentFileRequirement Requirement = DeploymentFileRequirement.Required);

public sealed record ConfigurationPatchRecord(
    string RelativePath,
    string Section,
    string Key,
    string? PreviousValue,
    string NewValue,
    string SchemaVersion,
    string ReleaseVersion);

public sealed class GameManifest
{
    public int SchemaVersion { get; set; } = 2;
    public string InstallId { get; set; } = string.Empty;
    public uint? SteamAppId { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<ManagedFile> Files { get; set; } = [];
    public List<ConfigurationPatchRecord> ConfigurationPatches { get; set; } = [];
    public List<string> TransactionIds { get; set; } = [];
    [JsonIgnore]
    public bool MetadataMigrated { get; set; }

    [JsonPropertyName("appId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint? LegacyAppId
    {
        get => null;
        set
        {
            if (value is null or 0) return;
            SteamAppId ??= value;
            if (string.IsNullOrWhiteSpace(InstallId))
                InstallId = GameInstallId.LegacySteam(value.Value).Value;
        }
    }
}

public sealed record TransactionRecord(
    string Id,
    string InstallId,
    string Action,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    bool RolledBack,
    IReadOnlyList<string> CompletedOperations,
    string? Error,
    uint? SteamAppId = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentOperationType
{
    CreateDirectory,
    Download,
    VerifySha256,
    VerifyPe,
    ExtractArchive,
    Backup,
    Copy,
    TrackExistingFile,
    Move,
    WriteIniValue,
    DeleteManagedFile,
    DeleteVerifiedFile,
    ForgetOwnership,
    ClearConfigurationPatches,
    RestoreBackup,
    VerifyFileState,
    VerifyIniValue,
    VerifyManagedLayout,
    VerifyComponentRemoved,
    WriteManifest
}

public sealed record DeploymentOperation(
    DeploymentOperationType Type,
    string Target,
    string? Source = null,
    string? Value = null,
    string? ExpectedSha256 = null,
    ComponentKind? Component = null,
    string? Description = null,
    string? BackupPath = null,
    string? SourceUrl = null,
    string? SourceBlobSha256 = null,
    string? SourceBundleSha256 = null,
    string? BundleRelativePath = null,
    string? ConfigurationSchema = null,
    string? ConfigurationVersion = null,
    bool ClearConfigurationPatch = false,
    string? Purpose = null,
    DeploymentFileRequirement Requirement = DeploymentFileRequirement.Required);

public sealed record DeploymentFileDecision(
    ComponentKind Component,
    DeploymentFileRequirement Requirement,
    DeploymentFileAction Action,
    string Reason,
    string? SourceRelease,
    string? SourceArchivePath,
    string DestinationPath,
    bool ReplacesManagedFile,
    bool PreservesExistingFile,
    bool AlternativeExists,
    IReadOnlyList<string> Alternatives);

public sealed record ComponentStateExpectation(ComponentKind Component, bool Installed);

public sealed class DeploymentPlan
{
    public required string Id { get; init; }
    public required string InstallId { get; init; }
    public uint? SteamAppId { get; init; }
    public required string GameRoot { get; init; }
    public required string DeploymentDirectory { get; init; }
    public long SelectionGeneration { get; set; }
    public required string Action { get; set; }
    public bool RequiresConfirmation { get; init; } = true;
    public bool RequiresRepair { get; set; }
    public string? CompatibilityMessage { get; set; }
    public string? LaunchOption { get; set; }
    public List<string> Warnings { get; init; } = [];
    public List<DeploymentOperation> Operations { get; init; } = [];
    public List<DeploymentFileDecision> FileDecisions { get; init; } = [];
    public List<ComponentStateExpectation> ExpectedComponentStates { get; init; } = [];
}

public enum OperationLifecycleState
{
    Preparing,
    Downloading,
    Validating,
    Applying,
    Verifying,
    Succeeded,
    SucceededWithWarning,
    FailedBeforeChanges,
    FailedAndRolledBack,
    FailedRollbackIncomplete,
    CancelledBeforeChanges,
    CancelledAndRolledBack
}

public sealed record ExecutionResult(
    bool Succeeded,
    bool DryRun,
    bool RolledBack,
    IReadOnlyList<string> Messages,
    string? Error = null,
    OperationLifecycleState State = OperationLifecycleState.Succeeded,
    string? Warning = null)
{
    public bool IsSuccessfulOutcome =>
        Succeeded && State is OperationLifecycleState.Succeeded or OperationLifecycleState.SucceededWithWarning;
}
