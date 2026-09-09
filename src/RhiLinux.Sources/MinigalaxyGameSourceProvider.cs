using System.Diagnostics;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class MinigalaxyGameSourceProvider : IGameSourceProvider
{
    public const string ProviderId = "minigalaxy";
    public string Id => ProviderId;
    public string DisplayName => "Minigalaxy";

    public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
        GameSourceDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<(string Path, SourceRootKind Kind)>
        {
            (Path.Combine(context.ConfigHome, "minigalaxy"), SourceRootKind.Xdg),
            (Path.Combine(context.HomeDirectory, ".config", "minigalaxy"), SourceRootKind.Native),
            (SourceRootDiscovery.FlatpakConfig(context.HomeDirectory, "io.github.sharkwouter.Minigalaxy", "minigalaxy"), SourceRootKind.Flatpak)
        };

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
            return SourceRecordFactory.EmptyScan(
                ProviderId,
                root,
                [new(ProviderId, code, SourceDiagnosticSeverity.Info, root.SkipReason ?? "Source root skipped.", root.CanonicalPath)],
                started.Elapsed);
        }

        var configPath = Path.Combine(root.CanonicalPath, "config.json");
        var gamesDir = Path.Combine(root.CanonicalPath, "games");
        var allGameFiles = Directory.Exists(gamesDir)
            ? Directory.GetFiles(gamesDir, "*.json").Order(StringComparer.Ordinal).ToArray()
            : [];
        var fingerprintInputs = new List<string?> { configPath };
        fingerprintInputs.AddRange(allGameFiles);
        var fingerprint = SourceRootDiscovery.SourceFingerprint(fingerprintInputs.ToArray()) + ":v1";
        var configChanged = context.IsTargeted && context.IncludesDocument(configPath);
        var gameFiles = context.IsTargeted && !configChanged
            ? allGameFiles.Where(context.IncludesDocument).ToArray()
            : allGameFiles;
        var metadataFiles = new List<string>();
        if (File.Exists(configPath)) metadataFiles.Add(configPath);
        metadataFiles.AddRange(gameFiles);

        if (!context.ForceFullScan && !context.IsTargeted &&
            context.PreviousFingerprints.TryGetValue(FingerprintKey(root), out var previous) &&
            string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return SourceRecordFactory.CachedScan(ProviderId, root, fingerprint, started.Elapsed, metadataFiles);
        }

        var diagnostics = new List<SourceDiagnostic>();
        var games = new List<SourceGameRecord>();
        var malformed = new List<SourceGameRecord>();
        var skipped = new List<SourceGameRecord>();

        string? installDir = null;
        if (File.Exists(configPath))
        {
            var (ok, document, error) = await SourceJson.TryLoadAsync(configPath, cancellationToken).ConfigureAwait(false);
            if (!ok || document is null)
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.SourceMetadataMalformed,
                    SourceDiagnosticSeverity.Warning,
                    "Minigalaxy config.json could not be parsed.",
                    error,
                    configPath));
                document?.Dispose();
            }
            else
            {
                using (document)
                {
                    installDir = SourceJson.GetString(document.RootElement, "install_dir", "installDir", "game_dir");
                }
            }
        }
        else
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMissing,
                SourceDiagnosticSeverity.Info,
                "Minigalaxy config.json was not found.",
                configPath,
                configPath));
        }

        if (string.IsNullOrWhiteSpace(installDir))
            installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GOG Games");

        foreach (var gameFile in gameFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (ok, document, error) = await SourceJson.TryLoadAsync(gameFile, cancellationToken).ConfigureAwait(false);
            if (!ok || document is null)
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.SourceMetadataMalformed,
                    SourceDiagnosticSeverity.Warning,
                    "Minigalaxy game status could not be parsed.",
                    error,
                    gameFile));
                malformed.Add(new(
                    ProviderId, GameStore.Gog, GameLauncher.Minigalaxy, Path.GetFileNameWithoutExtension(gameFile),
                    Path.GetFileNameWithoutExtension(gameFile), null, null, null, null,
                    GameBinaryPlatform.Unknown, CompatibilityEnvironment.Unknown, gameFile, null,
                    SourceRootDiscovery.GetMetadataTimestamp(gameFile), new Dictionary<string, string>(), [],
                    IsActionable: false, UnsupportedReason: "Malformed Minigalaxy status."));
                document?.Dispose();
                continue;
            }

            using (document)
            {
                var element = document.RootElement;
                var name = SourceJson.GetString(element, "name", "title", "game") ??
                           Path.GetFileNameWithoutExtension(gameFile);
                var id = SourceJson.GetString(element, "id", "game_id", "gog_id") ??
                         Path.GetFileNameWithoutExtension(gameFile);
                var status = SourceJson.GetString(element, "status", "state") ?? "installed";
                var platform = SourceRecordFactory.ParsePlatform(SourceJson.GetString(element, "platform", "os"));
                var executable = SourceJson.GetString(element, "executable", "exe");
                var installPath = SourceJson.GetString(element, "install_dir", "install_path", "path");
                if (string.IsNullOrWhiteSpace(installPath))
                    installPath = Path.Combine(installDir!, name);

                if (!string.Equals(status, "installed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "done", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(SourceRecordFactory.Create(
                        ProviderId, GameStore.Gog, GameLauncher.Minigalaxy, id, name, installPath, executable,
                        null, null, platform, CompatibilityEnvironment.Unknown, gameFile, configPath,
                        SourceRootDiscovery.GetMetadataTimestamp(gameFile),
                        isActionable: false, unsupportedReason: $"Minigalaxy status '{status}' is not installed."));
                    continue;
                }

                if (platform == GameBinaryPlatform.Unknown)
                {
                    if (executable is not null && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        platform = GameBinaryPlatform.Windows;
                    else if (Directory.Exists(installPath) &&
                             Directory.EnumerateFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly).Any())
                        platform = GameBinaryPlatform.Windows;
                    else if (File.Exists(Path.Combine(installPath, "start.sh")) ||
                             File.Exists(Path.Combine(installPath, "gameinfo")))
                        platform = GameBinaryPlatform.Linux;
                }

                var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                if (string.IsNullOrWhiteSpace(SourceJson.GetString(element, "id", "game_id", "gog_id")))
                    attributes["unknownGogId"] = "true";

                games.Add(SourceRecordFactory.Create(
                    ProviderId,
                    GameStore.Gog,
                    GameLauncher.Minigalaxy,
                    id,
                    name,
                    installPath,
                    executable,
                    null,
                    null,
                    platform == GameBinaryPlatform.Unknown ? GameBinaryPlatform.Windows : platform,
                    platform == GameBinaryPlatform.Linux ? CompatibilityEnvironment.Native : CompatibilityEnvironment.Wine,
                    gameFile,
                    configPath,
                    SourceRootDiscovery.GetMetadataTimestamp(gameFile),
                    attributes));
            }
        }

        return new GameSourceScanResult(
            ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
            fingerprint, started.Elapsed, false, true);
    }

    public static string FingerprintKey(GameSourceRoot root) => $"{ProviderId}:{root.CanonicalPath}";
}
