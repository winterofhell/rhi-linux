using System.Diagnostics;
using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed class SteamGameSourceProvider : IGameSourceProvider
{
    public const string ProviderId = "steam";
    private readonly SteamDiscoveryService discovery;

    public SteamGameSourceProvider(SteamDiscoveryService? discovery = null)
    {
        this.discovery = discovery ?? new SteamDiscoveryService();
    }

    public string Id => ProviderId;
    public string DisplayName => "Steam";

    public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
        GameSourceDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<(string Path, SourceRootKind Kind)>();
        if (context.Environment.TryGetValue("STEAM_DIR", out var steamDir) && !string.IsNullOrWhiteSpace(steamDir))
            candidates.Add((steamDir!, SourceRootKind.Environment));
        candidates.Add((Path.Combine(context.HomeDirectory, ".local", "share", "Steam"), SourceRootKind.Xdg));
        candidates.Add((Path.Combine(context.HomeDirectory, ".steam", "steam"), SourceRootKind.Native));
        candidates.Add((Path.Combine(context.HomeDirectory, ".steam", "root"), SourceRootKind.Legacy));
        candidates.Add((SourceRootDiscovery.FlatpakData(context.HomeDirectory, "com.valvesoftware.Steam", "Steam"), SourceRootKind.Flatpak));

        return Task.FromResult(SourceRootDiscovery.ResolveCandidates(ProviderId, candidates, context.RootsForProvider(ProviderId), context.HomeDirectory));
    }

    public async Task<GameSourceScanResult> ScanAsync(
        GameSourceRoot root,
        GameSourceScanContext context,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        if (!root.Exists || !root.Readable || root.Deduplicated)
        {
            var code = !root.Exists
                ? SourceDiagnosticCodes.SourceRootMissing
                : SourceDiagnosticCodes.SourceRootUnreadable;
            return new GameSourceScanResult(
                ProviderId,
                root,
                [],
                [new(ProviderId, code, SourceDiagnosticSeverity.Info, root.SkipReason ?? "Source root skipped.", root.CanonicalPath)],
                [],
                [],
                [],
                string.Empty,
                started.Elapsed,
                false,
                true);
        }

        var libraryFolders = Path.Combine(root.CanonicalPath, "steamapps", "libraryfolders.vdf");
        var diagnostics = new List<SourceDiagnostic>();
        var targetedManifests = context.Documents?
            .Where(path => Path.GetFileName(path).StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) &&
                           Path.GetExtension(path).Equals(".acf", StringComparison.OrdinalIgnoreCase))
            .Select(GameIdentity.NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
        var libraryFoldersChanged = context.Documents?.Any(path =>
            Path.GetFileName(path).Equals("libraryfolders.vdf", StringComparison.OrdinalIgnoreCase)) == true;
        var metadataFiles = context.IsTargeted && !libraryFoldersChanged
            ? targetedManifests?.Where(File.Exists).Order(StringComparer.Ordinal).ToList() ?? []
            : EnumerateMetadataFiles(root, libraryFolders, diagnostics);
        var fingerprint = context.IsTargeted &&
                          context.PreviousFingerprints.TryGetValue(FingerprintKey(root), out var previousFingerprint)
            ? previousFingerprint
            : SourceRootDiscovery.SourceFingerprint(metadataFiles.Cast<string?>().ToArray()) + ":v2";

        if (!context.ForceFullScan && !context.IsTargeted &&
            context.PreviousFingerprints.TryGetValue(FingerprintKey(root), out var previous) &&
            string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return new GameSourceScanResult(
                ProviderId, root, [], diagnostics, metadataFiles, [], [], fingerprint, started.Elapsed, true, false);
        }

        var games = new List<SourceGameRecord>();
        try
        {
            var scanRoots = targetedManifests is { Count: > 0 } && !libraryFoldersChanged
                ? targetedManifests.Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)
                    .Distinct(StringComparer.Ordinal).ToArray()
                : [root.CanonicalPath];
            var scan = await discovery.ScanAsync(
                scanRoots,
                null,
                cancellationToken,
                includeDefaultRoots: false,
                new ScanOptions(
                    UseLibraryIndex: false,
                    ForceFullAnalysis: context.ForceFullScan,
                    ManifestPaths: targetedManifests)).ConfigureAwait(false);

            foreach (var warning in scan.Warnings)
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.SourceRecordIncomplete,
                    SourceDiagnosticSeverity.Warning,
                    warning,
                    null,
                    libraryFolders));
            }

            foreach (var game in scan.Games)
            {
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appId"] = game.AppId.ToString(),
                    ["steamRoot"] = game.SteamRoot ?? string.Empty,
                    ["libraryRoot"] = game.LibraryRoot ?? string.Empty,
                    ["engine"] = game.Engine.ToString(),
                    ["confidence"] = game.Confidence.ToString(),
                    ["selectionReason"] = game.SelectionReason,
                    ["deploymentDirectory"] = game.DeploymentDirectory ?? game.GameRoot
                };
                games.Add(new SourceGameRecord(
                    ProviderId,
                    GameStore.Steam,
                    GameLauncher.Steam,
                    game.AppId.ToString(),
                    game.Name,
                    game.GameRoot,
                    game.Executable,
                    string.IsNullOrWhiteSpace(game.ProtonPrefix) ? null : game.ProtonPrefix,
                    null,
                    game.IsNativeLinux ? GameBinaryPlatform.Linux : GameBinaryPlatform.Windows,
                    CompatibilityEnvironment.Proton,
                    metadataFiles.FirstOrDefault(path =>
                        Path.GetFileName(path).Equals($"appmanifest_{game.AppId}.acf", StringComparison.Ordinal))
                        ?? libraryFolders,
                    null,
                    SourceRootDiscovery.GetMetadataTimestamp(metadataFiles.FirstOrDefault(path =>
                        Path.GetFileName(path).Equals($"appmanifest_{game.AppId}.acf", StringComparison.Ordinal))
                        ?? libraryFolders),
                    attributes,
                    [],
                    fingerprint,
                    game.RequiresConfirmation,
                    !game.IsNativeLinux,
                    game.IsNativeLinux ? "Native Linux / unsupported Steam installation." : null));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMalformed,
                SourceDiagnosticSeverity.Warning,
                "Steam discovery failed for this root.",
                exception.Message,
                root.CanonicalPath));
        }

        return new GameSourceScanResult(
            ProviderId, root, games, diagnostics, metadataFiles, [], [],
            fingerprint, started.Elapsed, false, true);
    }

    public static string FingerprintKey(GameSourceRoot root) => $"{ProviderId}:{root.CanonicalPath}";

    private static List<string> EnumerateMetadataFiles(
        GameSourceRoot root,
        string libraryFolders,
        ICollection<SourceDiagnostic> diagnostics)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var libraryRoots = new HashSet<string>(StringComparer.Ordinal) { root.CanonicalPath };
        if (File.Exists(libraryFolders))
        {
            files.Add(GameIdentity.NormalizePath(libraryFolders));
            try
            {
                var document = VdfParser.Parse(File.ReadAllText(libraryFolders));
                foreach (var path in SteamDiscoveryService.EnumerateLibraryPaths(document))
                    libraryRoots.Add(GameIdentity.NormalizePath(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.SourceMetadataMalformed,
                    SourceDiagnosticSeverity.Warning,
                    "Steam libraryfolders.vdf could not be enumerated for incremental tracking.",
                    exception.Message,
                    libraryFolders));
            }
        }

        foreach (var libraryRoot in libraryRoots.Order(StringComparer.Ordinal))
        {
            var steamApps = Path.Combine(libraryRoot, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf")
                             .Order(StringComparer.Ordinal))
                    files.Add(GameIdentity.NormalizePath(manifest));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.SourceRootUnreadable,
                    SourceDiagnosticSeverity.Warning,
                    "A Steam library could not be enumerated for incremental tracking.",
                    exception.Message,
                    steamApps));
            }
        }

        return files.Order(StringComparer.Ordinal).ToList();
    }
}
