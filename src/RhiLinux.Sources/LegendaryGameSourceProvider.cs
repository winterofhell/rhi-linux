using System.Diagnostics;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class LegendaryGameSourceProvider : IGameSourceProvider
{
    public const string ProviderId = "legendary";
    public string Id => ProviderId;
    public string DisplayName => "Legendary";

    public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
        GameSourceDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<(string Path, SourceRootKind Kind)>();
        if (context.Environment.TryGetValue("LEGENDARY_CONFIG_PATH", out var envPath) &&
            !string.IsNullOrWhiteSpace(envPath))
            candidates.Add((envPath!, SourceRootKind.Environment));
        candidates.Add((Path.Combine(context.ConfigHome, "legendary"), SourceRootKind.Xdg));
        candidates.Add((Path.Combine(context.HomeDirectory, ".config", "legendary"), SourceRootKind.Native));

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
                : !root.Readable
                    ? SourceDiagnosticCodes.SourceRootUnreadable
                    : SourceDiagnosticCodes.SourceRootMissing;
            return SourceRecordFactory.EmptyScan(
                ProviderId,
                root,
                [new(ProviderId, code, SourceDiagnosticSeverity.Info, root.SkipReason ?? "Source root skipped.", root.CanonicalPath)],
                started.Elapsed);
        }

        var installedPath = Path.Combine(root.CanonicalPath, "installed.json");
        var fingerprint = SourceRootDiscovery.SourceFingerprint(installedPath) + ":v1";
        var metadataFiles = new List<string>();
        if (File.Exists(installedPath))
            metadataFiles.Add(installedPath);

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

        if (!File.Exists(installedPath))
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMissing,
                SourceDiagnosticSeverity.Info,
                "Legendary installed.json was not found.",
                installedPath,
                installedPath));
            return new GameSourceScanResult(
                ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
                fingerprint, started.Elapsed, false, true);
        }

        var (ok, document, error) = await SourceJson.TryLoadAsync(installedPath, cancellationToken).ConfigureAwait(false);
        if (!ok || document is null)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMalformed,
                SourceDiagnosticSeverity.Warning,
                "Legendary installed.json could not be parsed.",
                error,
                installedPath));
            document?.Dispose();
            return new GameSourceScanResult(
                ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
                fingerprint, started.Elapsed, false, true);
        }

        using (document)
        {
            LegendaryInstalledParser.Parse(
                ProviderId,
                GameLauncher.Legendary,
                installedPath,
                document.RootElement,
                SourceRootDiscovery.GetMetadataTimestamp(installedPath),
                null,
                games,
                malformed,
                skipped,
                diagnostics);
        }

        return new GameSourceScanResult(
            ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
            fingerprint, started.Elapsed, false, true);
    }

    public static string FingerprintKey(GameSourceRoot root) => $"{ProviderId}:{root.CanonicalPath}";
}
