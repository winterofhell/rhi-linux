using System.Diagnostics;
using RhiLinux.Core;
using RhiLinux.Steam;

namespace RhiLinux.Sources;

public sealed record MultiSourceScanRequest(
    GameSourceDiscoveryContext? DiscoveryContext = null,
    IReadOnlySet<string>? EnabledProviders = null,
    bool ForceFullScan = false,
    int ProviderConcurrency = 0,
    int AnalysisConcurrency = 0,
    bool AnalyzeChangedWindowsInstalls = true,
    string? SourceIndexPath = null,
    string? LibraryDatabasePath = null);

public sealed record MultiSourceScanResult(
    IReadOnlyList<InstalledGame> Games,
    IReadOnlyList<DeploymentTarget> DeploymentTargets,
    IReadOnlyList<GameSourceScanResult> ProviderResults,
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    ReconciliationResult Reconciliation,
    IReadOnlyDictionary<string, double> TimingsMilliseconds,
    long ScanGeneration,
    bool UsedCache,
    int DocumentsParsed = 0,
    int InstallationsAnalyzed = 0,
    int InstallationsReconciled = 0);

public sealed class MultiSourceLibraryService(
    IEnumerable<IGameSourceProvider> providers,
    IGameAnalyzer? analyzer = null,
    ILibraryIndexStore? indexStore = null,
    ISourceIndexStore? sourceIndexStore = null)
{
    private readonly IReadOnlyList<IGameSourceProvider> providers = providers.ToArray();
    private readonly IGameAnalyzer analyzer = analyzer ?? new GameAnalyzer();

    public async Task<MultiSourceScanResult> ScanAsync(
        MultiSourceScanRequest request,
        CancellationToken cancellationToken = default)
    {
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        var total = Stopwatch.StartNew();
        var discoveryContext = request.DiscoveryContext ?? SourceRootDiscovery.CreateContext(
            enabledProviders: request.EnabledProviders);

        var sourceStore = sourceIndexStore
            ?? (string.IsNullOrWhiteSpace(request.SourceIndexPath)
                ? null
                : new JsonSourceIndexStore(request.SourceIndexPath!))
            ?? CreateDefaultSourceIndexStore();

        var cacheLoad = Stopwatch.StartNew();
        SourceIndexDocument previous;
        SqliteLibraryIndex? sqliteIndex = null;
        SourceDiagnostic? sqliteLoadDiagnostic = null;
        try
        {
            var dbPath = request.LibraryDatabasePath ?? new XdgPaths().LibraryDatabaseFile;
            sqliteIndex = new SqliteLibraryIndex(dbPath);
            await sqliteIndex.OpenAsync(cancellationToken).ConfigureAwait(false);
            await sqliteIndex.QuarantineIfCorruptAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.SourceIndexPath))
                await sqliteIndex.ImportJsonSourceIndexAsync(request.SourceIndexPath!, cancellationToken)
                    .ConfigureAwait(false);
            var cachedFingerprints = await sqliteIndex.LoadFingerprintsAsync(cancellationToken).ConfigureAwait(false);
            var cachedRecords = await sqliteIndex.LoadCachedRecordsAsync(cancellationToken).ConfigureAwait(false);
            if (cachedFingerprints.Count > 0 || cachedRecords.Count > 0)
            {
                previous = new SourceIndexDocument
                {
                    Fingerprints = cachedFingerprints,
                    Records = cachedRecords.ToList(),
                    ScanGeneration = 0
                };
            }
            else
            {
                previous = await sourceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            previous = await sourceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            sqliteLoadDiagnostic = new SourceDiagnostic(
                "multi-source",
                SourceDiagnosticCodes.SourceDatabaseReadFailed,
                SourceDiagnosticSeverity.Warning,
                "SQLite library index could not be loaded; falling back to JSON source index.",
                exception.Message);
            sqliteIndex = null;
        }

        timings["cacheLoad"] = cacheLoad.Elapsed.TotalMilliseconds;

        var enabled = request.EnabledProviders ?? discoveryContext.EnabledProviders;
        var activeProviders = providers
            .Where(provider => enabled is null || enabled.Count == 0 || enabled.Contains(provider.Id))
            .ToArray();

        var rootDiscovery = Stopwatch.StartNew();
        var rootsByProvider = new Dictionary<string, IReadOnlyList<GameSourceRoot>>(StringComparer.Ordinal);
        foreach (var provider in activeProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rootsByProvider[provider.Id] = await provider.DiscoverRootsAsync(discoveryContext, cancellationToken)
                .ConfigureAwait(false);
        }

        timings["rootDiscovery"] = rootDiscovery.Elapsed.TotalMilliseconds;

        var scanContext = new GameSourceScanContext(
            previous.Fingerprints,
            request.ForceFullScan,
            SourceIndexDocument.CurrentSchemaVersion);

        var providerConcurrency = request.ProviderConcurrency > 0
            ? request.ProviderConcurrency
            : Math.Clamp(Environment.ProcessorCount, 1, 4);
        using var gate = new SemaphoreSlim(providerConcurrency, providerConcurrency);
        var parseStarted = Stopwatch.StartNew();
        var scanTasks = new List<Task<GameSourceScanResult>>();
        var providerResults = new List<GameSourceScanResult>();
        foreach (var provider in activeProviders)
        {
            if (!rootsByProvider.TryGetValue(provider.Id, out var roots))
                continue;
            foreach (var root in roots)
            {
                if (root.Deduplicated)
                    continue;
                if (!root.Exists || !root.Readable)
                {
                    var code = !root.Exists
                        ? SourceDiagnosticCodes.SourceRootMissing
                        : SourceDiagnosticCodes.SourceRootUnreadable;
                    providerResults.Add(new GameSourceScanResult(
                        provider.Id,
                        root,
                        [],
                        [new(provider.Id, code, SourceDiagnosticSeverity.Info, root.SkipReason ?? "Source root skipped.", root.CanonicalPath)],
                        [],
                        [],
                        [],
                        previous.Fingerprints.GetValueOrDefault($"{provider.Id}:{root.CanonicalPath}") ?? string.Empty,
                        TimeSpan.Zero,
                        true,
                        false));
                    continue;
                }

                scanTasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        return await provider.ScanAsync(root, scanContext, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }
        }

        providerResults.AddRange(await Task.WhenAll(scanTasks).ConfigureAwait(false));
        timings["metadataParsing"] = parseStarted.Elapsed.TotalMilliseconds;

        var records = new List<SourceGameRecord>();
        var diagnostics = new List<SourceDiagnostic>();
        if (sqliteLoadDiagnostic is not null)
            diagnostics.Add(sqliteLoadDiagnostic);
        var fingerprints = new Dictionary<string, string>(previous.Fingerprints, StringComparer.Ordinal);
        var usedCache = providerResults.Count > 0;

        foreach (var result in providerResults)
        {
            diagnostics.AddRange(result.Diagnostics);
            var key = $"{result.ProviderId}:{result.Root.CanonicalPath}";
            fingerprints[key] = result.SourceFingerprint;
            if (result.FromCache)
            {
                records.AddRange(previous.Records.Where(record =>
                    record.ProviderId == result.ProviderId && MatchesRoot(record, result.Root)));
            }
            else if (result.Games.Count == 0 &&
                     result.Diagnostics.Any(diagnostic =>
                         diagnostic.Code is SourceDiagnosticCodes.SourceMetadataMalformed
                             or SourceDiagnosticCodes.SourceDatabaseLocked
                             or SourceDiagnosticCodes.SourceDatabaseReadFailed) &&
                     previous.Records.Any(record =>
                         record.ProviderId == result.ProviderId && MatchesRoot(record, result.Root)))
            {
                var stale = previous.Records
                    .Where(record => record.ProviderId == result.ProviderId && MatchesRoot(record, result.Root))
                    .Select(record => record with
                    {
                        Attributes = new Dictionary<string, string>(record.Attributes, StringComparer.Ordinal)
                        {
                            ["stale"] = "true"
                        }
                    })
                    .ToArray();
                records.AddRange(stale);
                diagnostics.Add(new(
                    result.ProviderId,
                    SourceDiagnosticCodes.SourceRecordIncomplete,
                    SourceDiagnosticSeverity.Warning,
                    "Provider scan failed; retaining last-known-good records as stale.",
                    result.Root.CanonicalPath));
            }
            else
            {
                records.AddRange(result.Games);
                usedCache = false;
            }

            if (!result.FromCache)
                usedCache = false;
        }

        if (providerResults.Count == 0)
            usedCache = false;
        else if (providerResults.Any(result => !result.FromCache))
            usedCache = false;
        else
            usedCache = true;

        var reconcileStarted = Stopwatch.StartNew();
        var reconciliation = GameReconciliation.Reconcile(records);
        diagnostics.AddRange(reconciliation.Diagnostics);
        timings["reconciliation"] = reconcileStarted.Elapsed.TotalMilliseconds;

        var games = reconciliation.Games.ToList();
        games = EnrichFromSourceAttributes(games);
        games = MarkStaleFromAttributes(games);
        var analyzedCount = 0;
        if (request.AnalyzeChangedWindowsInstalls)
        {
            var analysisStarted = Stopwatch.StartNew();
            (games, analyzedCount) = await AnalyzeChangedAsync(
                games, previous, request.ForceFullScan, request.AnalysisConcurrency, cancellationToken)
                .ConfigureAwait(false);
            timings["changedGameAnalysis"] = analysisStarted.Elapsed.TotalMilliseconds;
        }

        var generation = previous.ScanGeneration + 1;
        var document = new SourceIndexDocument
        {
            ScanGeneration = generation,
            Fingerprints = fingerprints,
            Records = records,
            Diagnostics = diagnostics.Take(500).ToList(),
            TimingsMilliseconds = timings
        };
        await sourceStore.SaveAsync(document, cancellationToken).ConfigureAwait(false);

        var documentsParsed = providerResults.Count(result => !result.FromCache && result.SourceChanged);

        try
        {
            if (sqliteIndex is null)
            {
                var dbPath = request.LibraryDatabasePath ?? new XdgPaths().LibraryDatabaseFile;
                sqliteIndex = new SqliteLibraryIndex(dbPath);
                await sqliteIndex.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            var sqliteGeneration = await sqliteIndex.BeginGenerationAsync(request.ForceFullScan, cancellationToken)
                .ConfigureAwait(false);
            await sqliteIndex.CompleteGenerationAsync(
                sqliteGeneration,
                fingerprints,
                records,
                games,
                diagnostics,
                timings,
                cancellationToken).ConfigureAwait(false);
            generation = sqliteGeneration;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            diagnostics.Add(new(
                "multi-source",
                SourceDiagnosticCodes.SourceDatabaseReadFailed,
                SourceDiagnosticSeverity.Warning,
                "SQLite library index could not be updated; JSON source index was still saved.",
                exception.Message));
        }
        finally
        {
            if (sqliteIndex is not null)
                await sqliteIndex.DisposeAsync().ConfigureAwait(false);
        }

        if (indexStore is not null)
        {
            try
            {
                var library = await indexStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                library.ScanGeneration = generation;
                library.UpdatedUtc = DateTimeOffset.UtcNow;
                await indexStore.SaveAsync(library, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(
                    "multi-source",
                    SourceDiagnosticCodes.SourceRecordIncomplete,
                    SourceDiagnosticSeverity.Info,
                    "Library index could not be updated after multi-source scan.",
                    exception.Message));
            }
        }

        timings["total"] = total.Elapsed.TotalMilliseconds;
        var deploymentTargets = games.Select(game => game.ToDeploymentTarget()).ToArray();
        return new MultiSourceScanResult(
            games,
            deploymentTargets,
            providerResults,
            diagnostics,
            reconciliation with { Games = games },
            timings,
            generation,
            usedCache,
            documentsParsed,
            analyzedCount,
            games.Count);
    }

    public static IReadOnlyList<IGameSourceProvider> CreateDefaultProviders(
        SteamDiscoveryService? steamDiscovery = null) =>
    [
        new SteamGameSourceProvider(steamDiscovery),
        new HeroicGameSourceProvider(),
        new LegendaryGameSourceProvider(),
        new LutrisGameSourceProvider(),
        new BottlesGameSourceProvider(),
        new MinigalaxyGameSourceProvider(),
        new ManualGameSourceProvider()
    ];

    private async Task<(List<InstalledGame> Games, int AnalyzedCount)> AnalyzeChangedAsync(
        IReadOnlyList<InstalledGame> games,
        SourceIndexDocument previous,
        bool forceFullScan,
        int analysisConcurrency,
        CancellationToken cancellationToken)
    {
        var previousKeys = new HashSet<string>(
            previous.Records.Select(record => GameIdentity.PhysicalKey(record.InstallRoot, record.ExecutableHint)),
            StringComparer.Ordinal);

        var concurrency = analysisConcurrency > 0
            ? analysisConcurrency
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var analyzed = 0;
        var tasks = games.Select(async game =>
        {
            if (!game.IsActionable || game.Platform != GameBinaryPlatform.Windows ||
                string.IsNullOrWhiteSpace(game.CanonicalInstallRoot) ||
                !Directory.Exists(game.CanonicalInstallRoot) ||
                game.PrimaryLauncher == GameLauncher.Steam)
                return game;

            var key = GameIdentity.PhysicalKey(game.CanonicalInstallRoot, game.Executable);
            var shouldAnalyze = forceFullScan || !previousKeys.Contains(key);
            if (!shouldAnalyze)
                return game;

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref analyzed);
                var analysis = await analyzer.AnalyzeAsync(
                    new GameInstall(
                        game.Store.ToString().ToLowerInvariant(),
                        game.ExternalId ?? game.InstallId.Value,
                        game.Name,
                        game.CanonicalInstallRoot),
                    new GameAnalysisOptions(),
                    cancellationToken).ConfigureAwait(false);

                var candidates = analysis.Fingerprint.Executables
                    .Select(item => new ExecutableCandidate(
                        item.Path, item.Score, item.Confidence, item.Architecture, item.Size, item.Reasons))
                    .ToArray();

                var executable = game.Executable;
                if ((string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) &&
                    analysis.PrimaryExecutable is not null)
                    executable = analysis.PrimaryExecutable.Path;

                var deployment = executable is not null
                    ? Path.GetDirectoryName(executable) ?? game.CanonicalInstallRoot
                    : game.DeploymentDirectory ?? game.CanonicalInstallRoot;

                return game with
                {
                    Executable = executable,
                    DeploymentDirectory = deployment,
                    Fingerprint = analysis.Fingerprint,
                    Engine = analysis.Fingerprint.Engine,
                    Candidates = candidates,
                    Confidence = executable is null
                        ? DetectionConfidence.None
                        : analysis.PrimaryExecutable is null ? DetectionConfidence.Medium : DetectionConfidence.High,
                    SelectionReason = executable is null
                        ? "No Windows executable found during analysis."
                        : game.Executable is not null && File.Exists(game.Executable)
                            ? "Selected from launcher metadata and validated by analysis."
                            : "Selected by shared game analysis.",
                    RequiresConfirmation = game.RequiresConfirmation || analysis.Fingerprint.AntiCheat.RequiresConfirmation
                };
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var result = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
        return (result, analyzed);
    }

    private static List<InstalledGame> MarkStaleFromAttributes(IReadOnlyList<InstalledGame> games)
    {
        var marked = new List<InstalledGame>(games.Count);
        foreach (var game in games)
        {
            var stale = game.IsStale ||
                game.Sources.Any(source =>
                    source.Attributes.TryGetValue("stale", out var value) &&
                    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
            marked.Add(stale && !game.IsStale ? game with { IsStale = true } : game);
        }

        return marked;
    }

    private static List<InstalledGame> EnrichFromSourceAttributes(IReadOnlyList<InstalledGame> games)
    {
        var enriched = new List<InstalledGame>(games.Count);
        foreach (var game in games)
        {
            var primary = game.Sources.FirstOrDefault(source => source.Launcher == game.PrimaryLauncher)
                ?? game.Sources.FirstOrDefault();
            if (primary is null)
            {
                enriched.Add(game);
                continue;
            }

            var engine = game.Engine;
            if (engine == GameEngine.Unknown &&
                primary.Attributes.TryGetValue("engine", out var engineRaw) &&
                Enum.TryParse<GameEngine>(engineRaw, true, out var parsedEngine))
                engine = parsedEngine;

            var confidence = game.Confidence;
            if (primary.Attributes.TryGetValue("confidence", out var confidenceRaw) &&
                Enum.TryParse<DetectionConfidence>(confidenceRaw, true, out var parsedConfidence))
                confidence = parsedConfidence;

            var reason = game.SelectionReason;
            if (primary.Attributes.TryGetValue("selectionReason", out var selectionReason) &&
                !string.IsNullOrWhiteSpace(selectionReason))
                reason = selectionReason;

            var deployment = game.DeploymentDirectory;
            if (primary.Attributes.TryGetValue("deploymentDirectory", out var deploymentDirectory) &&
                !string.IsNullOrWhiteSpace(deploymentDirectory))
                deployment = deploymentDirectory;

            enriched.Add(game with
            {
                Engine = engine,
                Confidence = confidence,
                SelectionReason = reason,
                DeploymentDirectory = deployment
            });
        }

        return enriched;
    }

    private static bool MatchesRoot(SourceGameRecord record, GameSourceRoot root)
    {
        if (string.IsNullOrWhiteSpace(record.MetadataPath)) return false;
        return record.MetadataPath.StartsWith(root.CanonicalPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               string.Equals(record.MetadataPath, root.CanonicalPath, StringComparison.Ordinal) ||
               string.Equals(Path.GetDirectoryName(record.MetadataPath), root.CanonicalPath, StringComparison.Ordinal);
    }

    private static ISourceIndexStore CreateDefaultSourceIndexStore()
    {
        var paths = new XdgPaths();
        return new JsonSourceIndexStore(Path.Combine(paths.AppDataDirectory, "source-index.json"));
    }
}
