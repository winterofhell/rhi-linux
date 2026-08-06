using System.Text.Json.Serialization;

namespace RhiLinux.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameStore
{
    Steam,
    Epic,
    Gog,
    Amazon,
    Other,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameLauncher
{
    Steam,
    Heroic,
    Legendary,
    Lutris,
    Bottles,
    Minigalaxy,
    Manual
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameBinaryPlatform
{
    Windows,
    Linux,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompatibilityEnvironment
{
    Proton,
    Wine,
    Umu,
    Native,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceRootKind
{
    Explicit,
    Environment,
    Native,
    Xdg,
    Flatpak,
    Custom,
    Legacy
}

public static class SourceDiagnosticCodes
{
    public const string SourceRootMissing = "SourceRootMissing";
    public const string SourceRootUnreadable = "SourceRootUnreadable";
    public const string SourceMetadataMissing = "SourceMetadataMissing";
    public const string SourceMetadataMalformed = "SourceMetadataMalformed";
    public const string SourceSchemaUnsupported = "SourceSchemaUnsupported";
    public const string SourceDatabaseLocked = "SourceDatabaseLocked";
    public const string SourceDatabaseReadFailed = "SourceDatabaseReadFailed";
    public const string SourceRecordIncomplete = "SourceRecordIncomplete";
    public const string InstallRootMissing = "InstallRootMissing";
    public const string ExecutableMissing = "ExecutableMissing";
    public const string ExecutableOutsideInstallRoot = "ExecutableOutsideInstallRoot";
    public const string PrefixMissing = "PrefixMissing";
    public const string UnsupportedPlatform = "UnsupportedPlatform";
    public const string UnsupportedRunner = "UnsupportedRunner";
    public const string DuplicateSourceRecord = "DuplicateSourceRecord";
    public const string DuplicatePhysicalInstall = "DuplicatePhysicalInstall";
    public const string LowConfidenceProgram = "LowConfidenceProgram";
    public const string DlcSkipped = "DlcSkipped";
    public const string ThirdPartyManaged = "ThirdPartyManaged";
}

public sealed record SourceDiagnostic(
    string ProviderId,
    string Code,
    SourceDiagnosticSeverity Severity,
    string Message,
    string? TechnicalDetail = null,
    string? MetadataPath = null,
    string? ExternalId = null);

public sealed record SourceGameRecord(
    string ProviderId,
    GameStore Store,
    GameLauncher Launcher,
    string ExternalId,
    string Name,
    string? InstallRoot,
    string? ExecutableHint,
    string? PrefixHint,
    string? WorkingDirectoryHint,
    GameBinaryPlatform Platform,
    CompatibilityEnvironment Environment,
    string MetadataPath,
    string? ConfigurationPath,
    DateTimeOffset MetadataModifiedAt,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    string? SourceFingerprint = null,
    bool RequiresConfirmation = false,
    bool IsActionable = true,
    string? UnsupportedReason = null);

public sealed record InstalledGame(
    GameInstallId InstallId,
    string Name,
    GameStore Store,
    GameLauncher PrimaryLauncher,
    string? ExternalId,
    uint? SteamAppId,
    string CanonicalInstallRoot,
    string? Executable,
    string? Prefix,
    string? DeploymentDirectory,
    GameBinaryPlatform Platform,
    CompatibilityEnvironment Environment,
    IReadOnlyList<SourceGameRecord> Sources,
    GameFingerprint? Fingerprint,
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    DetectionConfidence Confidence = DetectionConfidence.None,
    GameEngine Engine = GameEngine.Unknown,
    IReadOnlyList<ExecutableCandidate>? Candidates = null,
    bool RequiresConfirmation = false,
    bool IsActionable = true,
    string? UnsupportedReason = null,
    string SelectionReason = "",
    IReadOnlyList<FieldSelectionEvidence>? FieldSelections = null,
    bool IsStale = false,
    string? SteamRoot = null,
    string? LibraryRoot = null,
    SteamInstallState InstallState = SteamInstallState.Installed)
{
    public string EffectiveInstallId => InstallId.Value;
    public string GameRoot => CanonicalInstallRoot;
    public string ProtonPrefix => Prefix ?? string.Empty;
    public uint AppId => SteamAppId ?? 0;
    public GameLauncher Launcher => PrimaryLauncher;
    public bool IsNativeLinux => Platform == GameBinaryPlatform.Linux;
    public bool HasProtonPrefix =>
        !string.IsNullOrWhiteSpace(Prefix) && Directory.Exists(Prefix);
    public IReadOnlyList<ExecutableCandidate> CandidateList => Candidates ?? [];

    public string StoreBadge => Store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic",
        GameStore.Gog => "GOG",
        GameStore.Amazon => "Amazon",
        GameStore.Other => "Other",
        _ => "Unknown"
    };

    public string LauncherBadge => PrimaryLauncher.ToString();

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
            if (IsStale) parts.Add("Stale");
            return string.Join(" · ", parts);
        }
    }

    public DeploymentTarget ToDeploymentTarget() => new(
        InstallId,
        Name,
        CanonicalInstallRoot,
        Executable,
        DeploymentDirectory ?? CanonicalInstallRoot,
        Prefix,
        SteamAppId,
        Store,
        PrimaryLauncher,
        ExternalId,
        Confidence,
        SelectionReason,
        Engine,
        CandidateList,
        RequiresConfirmation,
        IsNativeLinux,
        HasProtonPrefix,
        Platform,
        Environment,
        IsActionable,
        UnsupportedReason,
        IsStale);

    public static InstalledGame FromSteamGame(SteamGame game) => new(
        new GameInstallId(game.EffectiveInstallId),
        game.Name,
        game.Store,
        game.Launcher,
        game.ExternalId ?? game.SteamAppId?.ToString(),
        game.SteamAppId,
        game.GameRoot,
        game.Executable,
        string.IsNullOrWhiteSpace(game.ProtonPrefix) ? null : game.ProtonPrefix,
        game.DeploymentDirectory,
        game.IsNativeLinux || game.Platform == GameBinaryPlatform.Linux
            ? GameBinaryPlatform.Linux
            : game.Platform,
        game.IsNativeLinux ? CompatibilityEnvironment.Native : game.Environment,
        game.Sources ?? [],
        null,
        game.SourceDiagnostics ?? [],
        game.Confidence,
        game.Engine,
        game.Candidates,
        game.RequiresConfirmation,
        game.IsNativeLinux ? false : game.IsActionable,
        game.UnsupportedReason ?? (game.IsNativeLinux
            ? "Native Linux / unsupported: no Windows executable was detected."
            : null),
        game.SelectionReason,
        null,
        false,
        game.SteamRoot,
        game.LibraryRoot,
        game.InstallState);

    public static InstalledGame FromPersistedGameEntry(PersistedGameEntry entry) => new(
        new GameInstallId(entry.InstallId),
        entry.Name,
        entry.Store,
        entry.Launcher,
        entry.ExternalId,
        entry.SteamAppId,
        entry.InstallRoot,
        entry.Executable,
        entry.Prefix,
        entry.DeploymentDirectory,
        entry.Platform,
        entry.Environment,
        [],
        null,
        [],
        entry.Confidence,
        entry.Engine,
        [],
        entry.RequiresConfirmation,
        entry.IsActionable,
        entry.UnsupportedReason,
        entry.SelectionReason,
        null,
        entry.IsStale,
        entry.SteamRoot,
        entry.LibraryRoot);
}

public sealed record GameSourceRoot(
    string ProviderId,
    string OriginalPath,
    string ExpandedPath,
    string CanonicalPath,
    SourceRootKind Kind,
    bool Exists,
    bool Readable,
    bool Deduplicated,
    string? SkipReason,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record GameSourceDiscoveryContext(
    string HomeDirectory,
    string ConfigHome,
    string DataHome,
    string CacheHome,
    IReadOnlyDictionary<string, string?> Environment,
    IReadOnlyList<string> CustomRoots,
    IReadOnlySet<string>? EnabledProviders = null);

public sealed record GameSourceScanContext(
    IReadOnlyDictionary<string, string> PreviousFingerprints,
    bool ForceFullScan,
    int SchemaVersion);

public sealed record GameSourceScanResult(
    string ProviderId,
    GameSourceRoot Root,
    IReadOnlyList<SourceGameRecord> Games,
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    IReadOnlyList<string> MetadataFiles,
    IReadOnlyList<SourceGameRecord> MalformedRecords,
    IReadOnlyList<SourceGameRecord> SkippedRecords,
    string SourceFingerprint,
    TimeSpan Duration,
    bool FromCache,
    bool SourceChanged);

public interface IGameSourceProvider
{
    string Id { get; }
    string DisplayName { get; }

    Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
        GameSourceDiscoveryContext context,
        CancellationToken cancellationToken = default);

    Task<GameSourceScanResult> ScanAsync(
        GameSourceRoot root,
        GameSourceScanContext context,
        CancellationToken cancellationToken = default);
}

public static class GameIdentity
{
    public static GameInstallId CreateInstallId(
        GameStore store,
        GameLauncher launcher,
        string? externalId,
        string canonicalInstallRoot,
        string? executable = null) =>
        GameInstallId.Create(store, launcher, externalId, canonicalInstallRoot, executable);

    public static string PhysicalKey(string? installRoot, string? executable)
    {
        var root = NormalizePath(installRoot ?? string.Empty);
        var exe = NormalizePath(executable ?? string.Empty);
        if (string.IsNullOrWhiteSpace(root) && string.IsNullOrWhiteSpace(exe))
            return string.Empty;
        return $"{root}|{exe}";
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return path.Trim().TrimEnd('/', '\\');
        }
    }

    public static bool IsPathInsideRoot(string root, string candidate)
    {
        var normalizedRoot = NormalizePath(root);
        var normalizedCandidate = NormalizePath(candidate);
        if (string.IsNullOrWhiteSpace(normalizedRoot) || string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;
        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.Ordinal))
            return true;
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static string? ResolveRelativePath(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (Path.IsPathRooted(expanded))
            return NormalizePath(expanded);
        if (string.IsNullOrWhiteSpace(root)) return null;
        return NormalizePath(Path.Combine(root, expanded));
    }

    public static string MapWindowsPath(string bottleRoot, string windowsPath)
    {
        var normalized = windowsPath.Replace('\\', '/').Trim();
        if (normalized.Length >= 3 &&
            char.IsLetter(normalized[0]) &&
            normalized[1] == ':' &&
            (normalized[2] == '/' || normalized[2] == '\\'))
        {
            var relative = normalized[3..].TrimStart('/', '\\');
            return NormalizePath(Path.Combine(bottleRoot, "drive_c", relative));
        }

        if (normalized.StartsWith("/drive_c/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("drive_c/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalized[(normalized.IndexOf("drive_c", StringComparison.OrdinalIgnoreCase) + "drive_c".Length)..]
                .TrimStart('/', '\\');
            return NormalizePath(Path.Combine(bottleRoot, "drive_c", relative));
        }

        return ResolveRelativePath(bottleRoot, windowsPath) ?? NormalizePath(windowsPath);
    }
}
