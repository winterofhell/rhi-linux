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

        return Task.FromResult(SourceRootDiscovery.ResolveCandidates(ProviderId, candidates, context.CustomRoots));
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
        var steamApps = Path.Combine(root.CanonicalPath, "steamapps");
        var fingerprint = SourceRootDiscovery.SourceFingerprint(libraryFolders, steamApps) + ":v1";
        var metadataFiles = new List<string>();
        if (File.Exists(libraryFolders)) metadataFiles.Add(libraryFolders);
        if (Directory.Exists(steamApps))
        {
            try
            {
                metadataFiles.AddRange(Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").Order(StringComparer.Ordinal));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (!context.ForceFullScan &&
            context.PreviousFingerprints.TryGetValue(FingerprintKey(root), out var previous) &&
            string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return new GameSourceScanResult(
                ProviderId, root, [], [], metadataFiles, [], [], fingerprint, started.Elapsed, true, false);
        }

        var diagnostics = new List<SourceDiagnostic>();
        var games = new List<SourceGameRecord>();
        try
        {
            var scan = await discovery.ScanAsync(
                [root.CanonicalPath],
                null,
                cancellationToken,
                includeDefaultRoots: false,
                new ScanOptions(UseLibraryIndex: false, ForceFullAnalysis: context.ForceFullScan)).ConfigureAwait(false);

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
                    metadataFiles.FirstOrDefault(path => path.Contains($"appmanifest_{game.AppId}.acf", StringComparison.Ordinal))
                        ?? libraryFolders,
                    null,
                    SourceRootDiscovery.GetMetadataTimestamp(libraryFolders),
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
}
