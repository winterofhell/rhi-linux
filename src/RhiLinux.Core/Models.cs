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
    Conditional,
    Optional,
    SuppliedByGame,
    NeverReplace,
    ObsoleteManaged,
    UnrelatedArchiveContent
}

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
    bool HasProtonPrefix = true);

public sealed record SteamManifestDiagnostic(
    string ManifestPath,
    uint? AppId,
    string? Name,
    string? InstallDirectory,
    string? StateFlags,
    string LibraryRoot,
    string Disposition,
    string Reason);

public sealed record ScanResult(
    IReadOnlyList<SteamGame> Games,
    IReadOnlyList<string> SteamRoots,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SteamManifestDiagnostic>? ManifestDiagnostics = null);

public sealed record ComponentStatus(
    ComponentKind Component,
    ComponentHealth Health,
    string? Version,
    IReadOnlyList<string> Files,
    string Explanation,
    InstallationVerification Verification = InstallationVerification.None,
    string? Diagnostic = null);

public sealed class ApplicationState
{
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public const int CurrentSchemaVersion = 1;
    public DateTimeOffset LastScanUtc { get; set; }
    public List<SteamGame> DiscoveredGames { get; set; } = [];
    public Dictionary<uint, GameOverride> Overrides { get; set; } = [];
    public List<TransactionRecord> Transactions { get; set; } = [];
    public Dictionary<uint, List<string>> ArtifactReferencesByAppId { get; set; } = [];
}

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
    ManagedFileClass FileClass = ManagedFileClass.Unknown);

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
    public int SchemaVersion { get; set; } = 1;
    public uint AppId { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<ManagedFile> Files { get; set; } = [];
    public List<ConfigurationPatchRecord> ConfigurationPatches { get; set; } = [];
    public List<string> TransactionIds { get; set; } = [];
    [JsonIgnore]
    public bool MetadataMigrated { get; set; }
}

public sealed record TransactionRecord(
    string Id,
    uint AppId,
    string Action,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    bool RolledBack,
    IReadOnlyList<string> CompletedOperations,
    string? Error);

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
    bool ClearConfigurationPatch = false);

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
    public required uint AppId { get; init; }
    public required string GameRoot { get; init; }
    public required string DeploymentDirectory { get; init; }
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

public sealed record ExecutionResult(bool Succeeded, bool DryRun, bool RolledBack, IReadOnlyList<string> Messages, string? Error = null);
