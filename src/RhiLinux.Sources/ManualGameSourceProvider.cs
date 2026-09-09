using System.Diagnostics;
using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class ManualGameSourceProvider : IGameSourceProvider
{
    public const string ProviderId = "manual";
    public string Id => ProviderId;
    public string DisplayName => "Manual";

    public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
        GameSourceDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<(string Path, SourceRootKind Kind)>();
        var customRoots = context.RootsForProvider(ProviderId);
        if (customRoots.Count > 0)
        {
            foreach (var custom in customRoots)
                candidates.Add((custom, SourceRootKind.Custom));
        }
        else
        {
            candidates.Add((Path.Combine(context.ConfigHome, "rhi-linux", "manual-games.json"), SourceRootKind.Xdg));
            candidates.Add((Path.Combine(context.HomeDirectory, ".config", "rhi-linux", "manual-games.json"), SourceRootKind.Native));
        }

        var results = new List<GameSourceRoot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (original, kind) in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(original);
            string canonical;
            try
            {
                canonical = GameIdentity.NormalizePath(expanded);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                results.Add(new(ProviderId, original, expanded, expanded, kind, false, false, false,
                    $"Path could not be normalized: {exception.Message}"));
                continue;
            }

            var exists = File.Exists(canonical) || Directory.Exists(canonical);
            var readable = exists && (File.Exists(canonical) || SourceRootDiscovery.CanReadDirectory(canonical));
            var deduplicated = exists && !seen.Add(canonical);
            string? skip = null;
            if (!exists) skip = "Manual games file does not exist.";
            else if (!readable) skip = "Manual games path is not readable.";
            else if (deduplicated) skip = "Deduplicated manual games path.";
            results.Add(new(ProviderId, original, expanded, canonical, kind, exists, readable, deduplicated, skip));
        }

        return Task.FromResult<IReadOnlyList<GameSourceRoot>>(results);
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

        var filePath = File.Exists(root.CanonicalPath)
            ? root.CanonicalPath
            : Path.Combine(root.CanonicalPath, "manual-games.json");
        var fingerprint = SourceRootDiscovery.SourceFingerprint(filePath) + ":v1";
        var metadataFiles = File.Exists(filePath) ? new List<string> { filePath } : new List<string>();

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

        if (!File.Exists(filePath))
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMissing,
                SourceDiagnosticSeverity.Info,
                "Manual games JSON was not found.",
                filePath,
                filePath));
            return new GameSourceScanResult(
                ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
                fingerprint, started.Elapsed, false, true);
        }

        var (ok, document, error) = await SourceJson.TryLoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (!ok || document is null)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMalformed,
                SourceDiagnosticSeverity.Warning,
                "Manual games JSON could not be parsed.",
                error,
                filePath));
            document?.Dispose();
            return new GameSourceScanResult(
                ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
                fingerprint, started.Elapsed, false, true);
        }

        using (document)
        {
            var modified = SourceRootDiscovery.GetMetadataTimestamp(filePath);
            IEnumerable<JsonElement> entries = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray(),
                JsonValueKind.Object when document.RootElement.TryGetProperty("games", out var nested) &&
                                          nested.ValueKind == JsonValueKind.Array => nested.EnumerateArray(),
                _ => []
            };

            if (document.RootElement.ValueKind is not JsonValueKind.Array &&
                !(document.RootElement.ValueKind == JsonValueKind.Object &&
                  document.RootElement.TryGetProperty("games", out _)))
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.SourceMetadataMalformed,
                    SourceDiagnosticSeverity.Warning,
                    "Manual games JSON must be an array or an object with a games array.",
                    null,
                    filePath));
            }

            foreach (var element in entries)
            {
                var name = SourceJson.GetString(element, "name", "title");
                var executable = SourceJson.GetString(element, "executable", "exe");
                var installRoot = SourceJson.GetString(element, "installRoot", "install_root", "gameRoot", "root");
                var prefix = SourceJson.GetString(element, "prefix", "winePrefix", "protonPrefix");
                var deployment = SourceJson.GetString(element, "deploymentDirectory", "deployment_directory");
                var externalId = SourceJson.GetString(element, "externalId", "external_id", "id") ?? name ?? "manual";
                var store = ParseStore(SourceJson.GetString(element, "store"));
                var platform = SourceRecordFactory.ParsePlatform(SourceJson.GetString(element, "platform"));
                if (platform == GameBinaryPlatform.Unknown)
                    platform = GameBinaryPlatform.Windows;

                if (string.IsNullOrWhiteSpace(name))
                {
                    malformed.Add(new(
                        ProviderId, store, GameLauncher.Manual, externalId, externalId,
                        installRoot, executable, prefix, null, platform, CompatibilityEnvironment.Wine,
                        filePath, null, modified, new Dictionary<string, string>(), [],
                        IsActionable: false, UnsupportedReason: "Manual entry is missing a name."));
                    diagnostics.Add(new(
                        ProviderId,
                        SourceDiagnosticCodes.SourceRecordIncomplete,
                        SourceDiagnosticSeverity.Warning,
                        "Manual game entry is missing a name.",
                        null,
                        filePath,
                        externalId));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(installRoot) && !string.IsNullOrWhiteSpace(executable))
                    installRoot = Path.GetDirectoryName(GameIdentity.ResolveRelativePath(null, executable) ?? executable);

                var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["allowOutsideRoot"] = "true"
                };
                if (!string.IsNullOrWhiteSpace(deployment))
                    attributes["deploymentDirectory"] = deployment!;
                var tags = SourceJson.GetString(element, "tags");
                if (!string.IsNullOrWhiteSpace(tags))
                    attributes["tags"] = tags!;

                games.Add(SourceRecordFactory.Create(
                    ProviderId,
                    store,
                    GameLauncher.Manual,
                    externalId,
                    name!,
                    installRoot,
                    executable,
                    prefix,
                    null,
                    platform,
                    CompatibilityEnvironment.Wine,
                    filePath,
                    null,
                    modified,
                    attributes,
                    allowOutsideRoot: true));
            }
        }

        return new GameSourceScanResult(
            ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
            fingerprint, started.Elapsed, false, true);
    }

    public static string FingerprintKey(GameSourceRoot root) => $"{ProviderId}:{root.CanonicalPath}";

    private static GameStore ParseStore(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "steam" => GameStore.Steam,
            "epic" => GameStore.Epic,
            "gog" => GameStore.Gog,
            "amazon" => GameStore.Amazon,
            "other" => GameStore.Other,
            _ => GameStore.Unknown
        };
}
