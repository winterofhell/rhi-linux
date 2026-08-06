using System.Diagnostics;
using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class HeroicGameSourceProvider : IGameSourceProvider
{
    public const string ProviderId = "heroic";
    public string Id => ProviderId;
    public string DisplayName => "Heroic";

    public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
        GameSourceDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<(string Path, SourceRootKind Kind)>
        {
            (Path.Combine(context.ConfigHome, "heroic"), SourceRootKind.Xdg),
            (Path.Combine(context.HomeDirectory, ".config", "heroic"), SourceRootKind.Native),
            (SourceRootDiscovery.FlatpakConfig(context.HomeDirectory, "com.heroicgameslauncher.hgl", "heroic"), SourceRootKind.Flatpak)
        };

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
            return SourceRecordFactory.EmptyScan(
                ProviderId,
                root,
                [new(ProviderId, code, SourceDiagnosticSeverity.Info, root.SkipReason ?? "Source root skipped.", root.CanonicalPath)],
                started.Elapsed);
        }

        var epicPath = Path.Combine(root.CanonicalPath, "legendaryConfig", "legendary", "installed.json");
        var gogPath = Path.Combine(root.CanonicalPath, "gog_store", "installed.json");
        var amazonPath = Path.Combine(root.CanonicalPath, "nile_config", "nile", "installed.json");
        var gamesConfigDir = Path.Combine(root.CanonicalPath, "GamesConfig");
        var metadataFiles = new[] { epicPath, gogPath, amazonPath, gamesConfigDir }
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToList();

        var fingerprint = SourceRootDiscovery.SourceFingerprint(epicPath, gogPath, amazonPath, gamesConfigDir) + ":v1";
        if (!context.ForceFullScan &&
            context.PreviousFingerprints.TryGetValue(FingerprintKey(root), out var previous) &&
            string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return SourceRecordFactory.CachedScan(ProviderId, root, fingerprint, started.Elapsed, metadataFiles);
        }

        var diagnostics = new List<SourceDiagnostic>();
        var games = new List<SourceGameRecord>();
        var malformed = new List<SourceGameRecord>();
        var skipped = new List<SourceGameRecord>();
        var configs = await LoadGamesConfigAsync(gamesConfigDir, cancellationToken).ConfigureAwait(false);

        await ParseEpicAsync(epicPath, configs, games, malformed, skipped, diagnostics, cancellationToken).ConfigureAwait(false);
        await ParseGogAsync(gogPath, configs, games, malformed, skipped, diagnostics, cancellationToken).ConfigureAwait(false);
        await ParseAmazonAsync(amazonPath, configs, games, malformed, diagnostics, cancellationToken).ConfigureAwait(false);

        if (metadataFiles.Count == 0)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMissing,
                SourceDiagnosticSeverity.Info,
                "No Heroic installed metadata files were found.",
                root.CanonicalPath,
                root.CanonicalPath));
        }

        return new GameSourceScanResult(
            ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
            fingerprint, started.Elapsed, false, true);
    }

    public static string FingerprintKey(GameSourceRoot root) => $"{ProviderId}:{root.CanonicalPath}";

    private static async Task<Dictionary<string, HeroicGameConfig>> LoadGamesConfigAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HeroicGameConfig>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return result;

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (ok, document, _) = await SourceJson.TryLoadAsync(file, cancellationToken).ConfigureAwait(false);
            if (!ok || document is null) continue;
            using (document)
            {
                var root = document.RootElement;
                var appKey = Path.GetFileNameWithoutExtension(file);
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty(appKey, out var nested) && nested.ValueKind == JsonValueKind.Object)
                        result[appKey] = ReadConfig(nested, file);
                    else if (LooksLikeConfig(root))
                        result[appKey] = ReadConfig(root, file);
                    else
                    {
                        foreach (var property in root.EnumerateObject())
                        {
                            if (property.Value.ValueKind == JsonValueKind.Object && LooksLikeConfig(property.Value))
                                result[property.Name] = ReadConfig(property.Value, file);
                        }
                    }
                }
            }
        }

        return result;
    }

    private static bool LooksLikeConfig(JsonElement element) =>
        element.TryGetProperty("winePrefix", out _) ||
        element.TryGetProperty("targetExe", out _) ||
        element.TryGetProperty("wineVersion", out _) ||
        element.TryGetProperty("wrapperOptions", out _);

    private static HeroicGameConfig ReadConfig(JsonElement element, string path) =>
        new(
            SourceJson.GetString(element, "winePrefix", "wineprefix"),
            SourceJson.GetString(element, "targetExe", "alternateExe", "executable"),
            path);

    private async Task ParseEpicAsync(
        string path,
        Dictionary<string, HeroicGameConfig> configs,
        List<SourceGameRecord> games,
        List<SourceGameRecord> malformed,
        List<SourceGameRecord> skipped,
        List<SourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        var (ok, document, error) = await SourceJson.TryLoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!ok || document is null)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMalformed,
                SourceDiagnosticSeverity.Warning,
                "Heroic Epic installed.json could not be parsed.",
                error,
                path));
            document?.Dispose();
            return;
        }

        using (document)
        {
            LegendaryInstalledParser.Parse(
                ProviderId,
                GameLauncher.Heroic,
                path,
                document.RootElement,
                SourceRootDiscovery.GetMetadataTimestamp(path),
                appName =>
                {
                    if (!configs.TryGetValue(appName, out var config))
                        return (null, null, null);
                    return (config.WinePrefix, config.TargetExe, config.ConfigPath);
                },
                games,
                malformed,
                skipped,
                diagnostics);
        }
    }

    private async Task ParseGogAsync(
        string path,
        Dictionary<string, HeroicGameConfig> configs,
        List<SourceGameRecord> games,
        List<SourceGameRecord> malformed,
        List<SourceGameRecord> skipped,
        List<SourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        var (ok, document, error) = await SourceJson.TryLoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!ok || document is null)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMalformed,
                SourceDiagnosticSeverity.Warning,
                "Heroic GOG installed.json could not be parsed.",
                error,
                path));
            document?.Dispose();
            return;
        }

        using (document)
        {
            var modified = SourceRootDiscovery.GetMetadataTimestamp(path);
            foreach (var element in SourceJson.EnumerateObjectOrArray(document.RootElement, "installed"))
            {
                var appName = SourceJson.GetString(element, "appName", "app_name", "id", "product_id") ?? string.Empty;
                var title = SourceJson.GetString(element, "title", "name", "install_name") ?? appName;
                var installPath = SourceJson.GetString(element, "install_path", "installPath", "path");
                var executable = SourceJson.GetString(element, "executable", "exe", "launchCommand");
                var platform = SourceRecordFactory.ParsePlatform(SourceJson.GetString(element, "platform", "os"));
                var isDlc = SourceJson.GetBool(element, "is_dlc", "isDlc", "isDLC") ?? false;
                var attributes = SourceJson.CollectAttributes(element, "version", "buildId", "installedWithDLCs");

                if (string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(title))
                {
                    malformed.Add(Incomplete(GameStore.Gog, path, modified, "unknown-gog"));
                    diagnostics.Add(new(
                        ProviderId,
                        SourceDiagnosticCodes.SourceRecordIncomplete,
                        SourceDiagnosticSeverity.Warning,
                        "GOG installed record is missing identity fields.",
                        null,
                        path));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(appName))
                    appName = title!;

                if (isDlc)
                {
                    var dlc = SourceRecordFactory.Create(
                        ProviderId, GameStore.Gog, GameLauncher.Heroic, appName, title!, installPath, executable,
                        null, null, platform, CompatibilityEnvironment.Wine, path, null, modified, attributes,
                        [new(ProviderId, SourceDiagnosticCodes.DlcSkipped, SourceDiagnosticSeverity.Info,
                            $"Skipped GOG DLC '{title}'.", null, path, appName)],
                        isActionable: false, unsupportedReason: "DLC is not independently launchable.");
                    skipped.Add(dlc);
                    diagnostics.Add(dlc.Diagnostics[0]);
                    continue;
                }

                string? prefix = null;
                string? configPath = null;
                if (configs.TryGetValue(appName, out var config))
                {
                    prefix = config.WinePrefix;
                    configPath = config.ConfigPath;
                    if (!string.IsNullOrWhiteSpace(config.TargetExe))
                        executable = config.TargetExe;
                }

                if (platform == GameBinaryPlatform.Unknown &&
                    executable is not null &&
                    executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    platform = GameBinaryPlatform.Windows;
                if (platform == GameBinaryPlatform.Unknown)
                    platform = GameBinaryPlatform.Windows;

                games.Add(SourceRecordFactory.Create(
                    ProviderId, GameStore.Gog, GameLauncher.Heroic, appName, title!, installPath, executable,
                    prefix, null, platform, CompatibilityEnvironment.Wine, path, configPath, modified, attributes));
            }
        }
    }

    private async Task ParseAmazonAsync(
        string path,
        Dictionary<string, HeroicGameConfig> configs,
        List<SourceGameRecord> games,
        List<SourceGameRecord> malformed,
        List<SourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        var (ok, document, error) = await SourceJson.TryLoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!ok || document is null)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMalformed,
                SourceDiagnosticSeverity.Warning,
                "Heroic Amazon installed.json could not be parsed.",
                error,
                path));
            document?.Dispose();
            return;
        }

        using (document)
        {
            var modified = SourceRootDiscovery.GetMetadataTimestamp(path);
            foreach (var element in SourceJson.EnumerateObjectOrArray(document.RootElement, "installed"))
            {
                var appName = SourceJson.GetString(element, "id", "appName", "product_id", "asin") ?? string.Empty;
                var title = SourceJson.GetString(element, "title", "name", "productTitle") ?? appName;
                var installPath = SourceJson.GetString(element, "path", "install_path", "installPath");
                var executable = SourceJson.GetString(element, "executable", "exe");
                var platform = SourceRecordFactory.ParsePlatform(SourceJson.GetString(element, "platform"));
                var attributes = SourceJson.CollectAttributes(element, "version", "branch");

                if (string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(title))
                {
                    malformed.Add(Incomplete(GameStore.Amazon, path, modified, "unknown-amazon"));
                    diagnostics.Add(new(
                        ProviderId,
                        SourceDiagnosticCodes.SourceRecordIncomplete,
                        SourceDiagnosticSeverity.Warning,
                        "Amazon installed record is missing identity fields.",
                        null,
                        path));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(appName))
                    appName = title!;

                string? prefix = null;
                string? configPath = null;
                if (configs.TryGetValue(appName, out var config) ||
                    (title is not null && configs.TryGetValue(title, out config)))
                {
                    prefix = config.WinePrefix;
                    configPath = config.ConfigPath;
                    if (!string.IsNullOrWhiteSpace(config.TargetExe))
                        executable = config.TargetExe;
                }

                if (platform == GameBinaryPlatform.Unknown)
                    platform = GameBinaryPlatform.Windows;

                games.Add(SourceRecordFactory.Create(
                    ProviderId, GameStore.Amazon, GameLauncher.Heroic, appName, title!, installPath, executable,
                    prefix, null, platform, CompatibilityEnvironment.Wine, path, configPath, modified, attributes));
            }
        }
    }

    private static SourceGameRecord Incomplete(
        GameStore store,
        string metadataPath,
        DateTimeOffset modifiedAt,
        string externalId) =>
        new(
            ProviderId,
            store,
            GameLauncher.Heroic,
            externalId,
            externalId,
            null, null, null, null,
            GameBinaryPlatform.Unknown,
            CompatibilityEnvironment.Unknown,
            metadataPath,
            null,
            modifiedAt,
            new Dictionary<string, string>(),
            [],
            IsActionable: false,
            UnsupportedReason: "Malformed or incomplete source record.");

    private sealed record HeroicGameConfig(string? WinePrefix, string? TargetExe, string ConfigPath);
}
