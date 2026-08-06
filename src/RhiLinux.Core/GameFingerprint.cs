namespace RhiLinux.Core;

public enum ExecutableKind
{
    Unknown,
    Primary,
    Alternate,
    Launcher,
    Helper,
    Server,
    Benchmark,
    AntiCheat,
    Redistributable,
    Editor
}

public enum GraphicsApiKind
{
    Unknown,
    Direct3D9,
    Direct3D11,
    Direct3D12,
    Vulkan,
    OpenGl
}

public enum UpscalerKind
{
    Unknown,
    Dlss,
    Fsr,
    XeSS,
    OptiScaler
}

public enum AntiCheatKind
{
    None,
    EasyAntiCheat,
    BattlEye,
    Other
}

public enum EvidenceConfidence
{
    None,
    Low,
    Medium,
    High
}

public sealed record DiagnosticEvidence(
    string Type,
    string? Path,
    int Weight,
    string Reason,
    EvidenceConfidence Confidence,
    string RuleSource);

public sealed record ExecutableFingerprint(
    string Path,
    string RelativePath,
    ExecutableKind Kind,
    int Score,
    DetectionConfidence Confidence,
    PeArchitecture Architecture,
    long Size,
    DateTimeOffset LastWriteUtc,
    IReadOnlyList<string> Reasons);

public sealed record GraphicsApiEvidence(
    IReadOnlyList<GraphicsApiKind> Apis,
    IReadOnlyList<DiagnosticEvidence> Evidence);

public sealed record UpscalerEvidence(
    IReadOnlyList<UpscalerKind> Upscalers,
    IReadOnlyList<DiagnosticEvidence> Evidence);

public sealed record AntiCheatEvidence(
    AntiCheatKind Kind,
    bool RequiresConfirmation,
    IReadOnlyList<DiagnosticEvidence> Evidence);

public sealed record ComponentFootprints(
    bool HasReShade,
    bool HasRenoDx,
    bool HasOptiScaler,
    bool HasOptiPatcher,
    bool HasLuma,
    bool HasReFramework,
    bool HasDxvk,
    IReadOnlyList<string> RelevantPaths,
    IReadOnlyList<DiagnosticEvidence> Evidence);

public sealed record DirectoryFingerprint(
    string CanonicalRoot,
    long EntryCount,
    long SampleHash,
    long TopLevelMtimeUtcTicks,
    IReadOnlyList<string> SampleEntries);

public sealed record GameFingerprint(
    string GameRoot,
    string CanonicalRoot,
    DateTimeOffset ScannedAt,
    IReadOnlyList<ExecutableFingerprint> Executables,
    GameEngine Engine,
    GraphicsApiEvidence GraphicsApis,
    UpscalerEvidence Upscalers,
    AntiCheatEvidence AntiCheat,
    ComponentFootprints Components,
    IReadOnlyList<string> RelevantFiles,
    IReadOnlyList<DiagnosticEvidence> Evidence,
    DirectoryFingerprint DirectoryFingerprint,
    bool HasNativeLinuxExecutable,
    bool Incomplete,
    int FilesVisited,
    int DirectoriesVisited);

public sealed record GameInstall(
    string Store,
    string StoreGameId,
    string Name,
    string GameRoot,
    string? ManifestPath = null,
    long? ManifestSize = null,
    long? ManifestMtimeUtcTicks = null);

public sealed record GameAnalysisOptions(
    int MaxDepth = 7,
    int MaxFiles = 25_000,
    bool FollowSymlinks = false,
    bool OpenPeHeaders = true,
    bool CollectComponentMarkers = true);

public sealed record GameAnalysisResult(
    GameFingerprint Fingerprint,
    ExecutableFingerprint? PrimaryExecutable,
    IReadOnlyList<ExecutableFingerprint> AlternateExecutables,
    IReadOnlyList<DiagnosticEvidence> Diagnostics);

public interface IGameAnalyzer
{
    Task<GameAnalysisResult> AnalyzeAsync(
        GameInstall install,
        GameAnalysisOptions options,
        CancellationToken cancellationToken = default);
}

public static class GameAnalyzerVersions
{
    public const int SchemaVersion = 1;
}
