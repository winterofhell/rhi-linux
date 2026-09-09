using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    string? LibraryDatabasePath = null,
    LibraryRefreshScope? RefreshScope = null,
    IReadOnlyDictionary<string, GameOverride>? Overrides = null);

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
    int InstallationsReconciled = 0,
    LibraryMetrics? Metrics = null,
    IReadOnlyList<LibraryProviderStatus>? ProviderStatuses = null);

public sealed class MultiSourceLibraryService(
    IEnumerable<IGameSourceProvider> providers,
    IGameAnalyzer? analyzer = null,
    ISourceIndexStore? sourceIndexStore = null)
{
    private readonly IReadOnlyList<IGameSourceProvider> providers = providers.ToArray();
    private readonly IGameAnalyzer analyzer = analyzer ?? new GameAnalyzer();
    private readonly ISourceIndexStore? legacySourceIndexStore = sourceIndexStore;

    public async Task<MultiSourceScanResult> ScanAsync(
        MultiSourceScanRequest request,
        CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        var scope = request.RefreshScope ?? LibraryRefreshScope.Full(
            request.ForceFullScan ? "Forced full scan" : "Library refresh",
            request.ForceFullScan);
        if (request.ForceFullScan && !scope.Force) scope = scope with { Force = true };
        var discoveryContext = request.DiscoveryContext ?? SourceRootDiscovery.CreateContext(
            enabledProviders: request.EnabledProviders);
        var enabled = request.EnabledProviders ?? discoveryContext.EnabledProviders;
        var consideredProviders = providers
            .Where(provider => enabled is null || enabled.Count == 0 || enabled.Contains(provider.Id))
            .OrderBy(provider => provider.Id, StringComparer.Ordinal)
            .ToArray();
        var activeProviders = consideredProviders.Where(provider => scope.IncludesProvider(provider.Id)).ToArray();

        var diagnostics = new List<SourceDiagnostic>();
        var sqliteRead = Stopwatch.StartNew();
        SqliteLibraryIndex? sqliteIndex = null;
        var previousRecords = Array.Empty<SourceGameRecord>();
        var previousGames = Array.Empty<InstalledGame>();
        var previousDocuments = Array.Empty<SourceDocumentEntry>();
        var previousRelationships = Array.Empty<GameSourceRelationship>();
        IReadOnlyDictionary<GameInstallId, PersistedGameAnalysis> previousAnalyses =
            new Dictionary<GameInstallId, PersistedGameAnalysis>();
        var previousFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var previousGeneration = 0L;
        var reconciliationVersion = 0;
        try
        {
            var databasePath = ResolveDatabasePath(request);
            sqliteIndex = new SqliteLibraryIndex(databasePath);
            await sqliteIndex.OpenAsync(cancellationToken).ConfigureAwait(false);
            await sqliteIndex.QuarantineIfCorruptAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.SourceIndexPath))
                await sqliteIndex.ImportJsonSourceIndexAsync(request.SourceIndexPath!, cancellationToken).ConfigureAwait(false);
            else if (legacySourceIndexStore is not null &&
                     await legacySourceIndexStore.TryLoadAsync(cancellationToken).ConfigureAwait(false) is { } legacy &&
                     legacy.Records.Count > 0 && await sqliteIndex.GetActiveGenerationAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                var importedGeneration = await sqliteIndex.BeginGenerationAsync(false, cancellationToken).ConfigureAwait(false);
                await sqliteIndex.CompleteGenerationAsync(
                    importedGeneration,
                    legacy.Fingerprints,
                    legacy.Records,
                    [],
                    legacy.Diagnostics,
                    legacy.TimingsMilliseconds,
                    cancellationToken).ConfigureAwait(false);
            }

            previousFingerprints = await sqliteIndex.LoadFingerprintsAsync(cancellationToken).ConfigureAwait(false);
            previousRecords = (await sqliteIndex.LoadCachedRecordsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            previousGames = (await sqliteIndex.LoadCachedGamesAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            previousDocuments = (await sqliteIndex.LoadSourceDocumentsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            previousRelationships = (await sqliteIndex.LoadGameRelationshipsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            previousAnalyses = await sqliteIndex.LoadAnalysisAsync(cancellationToken).ConfigureAwait(false);
            previousGeneration = await sqliteIndex.GetActiveGenerationAsync(cancellationToken).ConfigureAwait(false);
            reconciliationVersion = await sqliteIndex.GetReconciliationVersionAsync(cancellationToken).ConfigureAwait(false);
            previousGames = RehydrateGames(previousGames, previousRecords, previousRelationships).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            diagnostics.Add(new(
                "multi-source",
                SourceDiagnosticCodes.SourceDatabaseReadFailed,
                SourceDiagnosticSeverity.Error,
                "The canonical SQLite library cache could not be loaded. The refresh will continue without cached state.",
                exception.Message));
            if (sqliteIndex is not null)
            {
                await sqliteIndex.DisposeAsync().ConfigureAwait(false);
                sqliteIndex = null;
            }
        }
        sqliteRead.Stop();
        timings["sqliteRead"] = sqliteRead.Elapsed.TotalMilliseconds;

        var rootsByProvider = new Dictionary<string, IReadOnlyList<GameSourceRoot>>(StringComparer.Ordinal);
        var rootDiscovery = Stopwatch.StartNew();
        foreach (var provider in activeProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                rootsByProvider[provider.Id] = await provider.DiscoverRootsAsync(discoveryContext, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(
                    provider.Id,
                    SourceDiagnosticCodes.SourceRootUnreadable,
                    SourceDiagnosticSeverity.Warning,
                    $"{provider.DisplayName} roots could not be discovered.",
                    exception.Message));
                rootsByProvider[provider.Id] = [];
            }
        }
        rootDiscovery.Stop();
        timings["rootDiscovery"] = rootDiscovery.Elapsed.TotalMilliseconds;

        var scanResults = await ScanProvidersAsync(
            activeProviders,
            rootsByProvider,
            previousFingerprints,
            scope,
            request.ProviderConcurrency,
            cancellationToken).ConfigureAwait(false);
        var providerResults = scanResults.Results.ToList();
        diagnostics.AddRange(providerResults.SelectMany(result => result.Diagnostics));
        timings["metadataParsing"] = scanResults.Elapsed.TotalMilliseconds;

        var fingerprints = new Dictionary<string, string>(previousFingerprints, StringComparer.Ordinal);
        foreach (var result in providerResults)
            fingerprints[$"{result.ProviderId}:{result.Root.CanonicalPath}"] = result.SourceFingerprint;

        var records = MergeRecords(previousRecords, providerResults, scope);
        var delta = CompareRecords(previousRecords, records);
        var mustFullyReconcile = scope.Force || previousGames.Length == 0 || previousRelationships.Length == 0 ||
                                  reconciliationVersion != GameReconciliation.RulesVersion;
        var reconcileStarted = Stopwatch.StartNew();
        var reconciliation = ReconcileIncrementally(
            previousGames,
            previousRecords,
            records,
            delta,
            mustFullyReconcile);
        var games = MarkStaleFromAttributes(EnrichFromSourceAttributes(reconciliation.Games));
        reconcileStarted.Stop();
        timings["reconciliation"] = reconcileStarted.Elapsed.TotalMilliseconds;

        var generation = previousGeneration + 1;
        var analysisStarted = Stopwatch.StartNew();
        var analysis = request.AnalyzeChangedWindowsInstalls
            ? await AnalyzeChangedAsync(
                games,
                previousAnalyses,
                request.Overrides,
                scope.Force,
                request.AnalysisConcurrency,
                generation,
                cancellationToken).ConfigureAwait(false)
            : new AnalysisResult(games.ToList(), previousAnalyses.Values.ToList(), 0, 0);
        games = analysis.Games;
        analysisStarted.Stop();
        timings["changedGameAnalysis"] = analysisStarted.Elapsed.TotalMilliseconds;

        var changedDocuments = BuildDocuments(
            previousDocuments,
            providerResults,
            scope,
            generation);
        var relationships = BuildRelationships(games, generation);
        var sqliteWrite = Stopwatch.StartNew();
        if (sqliteIndex is not null)
        {
            try
            {
                generation = await sqliteIndex.BeginGenerationAsync(scope.Force, cancellationToken).ConfigureAwait(false);
                changedDocuments = changedDocuments
                    .Select(document => document with
                    {
                        LastSuccessfulParseGeneration = document.LastSuccessfulParseGeneration == previousGeneration + 1
                            ? generation
                            : document.LastSuccessfulParseGeneration
                    })
                    .ToList();
                relationships = BuildRelationships(games, generation);
                var persistedAnalyses = analysis.Analyses
                    .Select(item => item.Generation == previousGeneration + 1 ? item with { Generation = generation } : item)
                    .ToArray();
                await sqliteIndex.CompleteGenerationAsync(
                    generation,
                    fingerprints,
                    records,
                    games,
                    diagnostics,
                    timings,
                    changedDocuments,
                    relationships,
                    persistedAnalyses,
                    GameReconciliation.RulesVersion,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                diagnostics.Add(new(
                    "multi-source",
                    SourceDiagnosticCodes.SourceDatabaseReadFailed,
                    SourceDiagnosticSeverity.Error,
                    "The canonical SQLite library cache could not commit this refresh.",
                    exception.Message));
            }
            finally
            {
                await sqliteIndex.DisposeAsync().ConfigureAwait(false);
            }
        }
        sqliteWrite.Stop();
        timings["sqliteWrite"] = sqliteWrite.Elapsed.TotalMilliseconds;

        total.Stop();
        timings["total"] = total.Elapsed.TotalMilliseconds;
        var libraryDelta = CompareGames(previousGames, games);
        var parsedDocuments = CountParsedDocuments(providerResults, scope);
        var stattedDocuments = CountStattedDocuments(providerResults, scope);
        var providerStatuses = BuildProviderStatuses(consideredProviders, rootsByProvider, providerResults, records);
        var metrics = new LibraryMetrics(
            scope.Reason,
            scope,
            consideredProviders.Select(provider => provider.Id).ToArray(),
            activeProviders.Select(provider => provider.Id).ToArray(),
            rootsByProvider.Values.Sum(roots => roots.Count(root => !root.Deduplicated)),
            stattedDocuments,
            parsedDocuments,
            delta.Added.Count,
            delta.Changed.Count,
            delta.Removed.Count,
            reconciliation.InstallationsReconciled,
            analysis.AnalyzedCount,
            libraryDelta.Added,
            libraryDelta.Updated,
            libraryDelta.Removed,
            providerResults.Count(result => result.FromCache) + analysis.CacheHits,
            providerResults.Count(result => !result.FromCache) + analysis.AnalyzedCount,
            analysis.DirectoryTraversals,
            sqliteRead.Elapsed,
            sqliteWrite.Elapsed,
            total.Elapsed);

        return new(
            games,
            games.Select(game => game.ToDeploymentTarget()).ToArray(),
            providerResults,
            diagnostics,
            new ReconciliationResult(
                games,
                reconciliation.Diagnostics,
                reconciliation.DuplicateSourceCount,
                reconciliation.DuplicatePhysicalInstallCount),
            timings,
            generation,
            delta.TotalChanges == 0 && analysis.AnalyzedCount == 0,
            parsedDocuments,
            analysis.AnalyzedCount,
            reconciliation.InstallationsReconciled,
            metrics,
            providerStatuses);
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

    private static async Task<ProviderScanBatch> ScanProvidersAsync(
        IReadOnlyList<IGameSourceProvider> activeProviders,
        IReadOnlyDictionary<string, IReadOnlyList<GameSourceRoot>> rootsByProvider,
        IReadOnlyDictionary<string, string> previousFingerprints,
        LibraryRefreshScope scope,
        int requestedConcurrency,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var results = new List<GameSourceScanResult>();
        var tasks = new List<Task<GameSourceScanResult>>();
        var concurrency = requestedConcurrency > 0 ? requestedConcurrency : Math.Clamp(Environment.ProcessorCount, 1, 4);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        foreach (var provider in activeProviders)
        {
            if (!rootsByProvider.TryGetValue(provider.Id, out var roots)) continue;
            foreach (var root in roots.Where(root => !root.Deduplicated && scope.IncludesRoot(root.CanonicalPath)))
            {
                if (!root.Exists || !root.Readable)
                {
                    var code = !root.Exists ? SourceDiagnosticCodes.SourceRootMissing : SourceDiagnosticCodes.SourceRootUnreadable;
                    results.Add(new(
                        provider.Id,
                        root,
                        [],
                        [new(provider.Id, code, SourceDiagnosticSeverity.Info,
                            root.SkipReason ?? "Source root skipped.", root.CanonicalPath)],
                        [], [], [], string.Empty, TimeSpan.Zero, true, false));
                    continue;
                }

                tasks.Add(ScanRootAsync(provider, root));
            }
        }

        results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        started.Stop();
        return new(results, started.Elapsed);

        async Task<GameSourceScanResult> ScanRootAsync(IGameSourceProvider provider, GameSourceRoot root)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var documents = scope.Documents is null
                    ? null
                    : scope.RootIds?.Contains(root.CanonicalPath) == true
                        ? scope.Documents.Select(GameIdentity.NormalizePath).ToHashSet(StringComparer.Ordinal)
                        : scope.Documents
                        .Where(path => IsPathForRoot(path, root))
                        .Select(GameIdentity.NormalizePath)
                        .ToHashSet(StringComparer.Ordinal);
                return await provider.ScanAsync(
                    root,
                    new(previousFingerprints, scope.Force, SourceIndexDocument.CurrentSchemaVersion,
                        documents, root.CanonicalPath),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or SqliteException)
            {
                return new(
                    provider.Id,
                    root,
                    [],
                    [new(provider.Id, SourceDiagnosticCodes.SourceMetadataMalformed,
                        SourceDiagnosticSeverity.Warning,
                        $"{provider.DisplayName} metadata refresh failed; the last successful records were retained.",
                        exception.Message,
                        root.CanonicalPath)],
                    [], [], [],
                    previousFingerprints.GetValueOrDefault($"{provider.Id}:{root.CanonicalPath}") ?? string.Empty,
                    TimeSpan.Zero,
                    true,
                    false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<AnalysisResult> AnalyzeChangedAsync(
        IReadOnlyList<InstalledGame> games,
        IReadOnlyDictionary<GameInstallId, PersistedGameAnalysis> previous,
        IReadOnlyDictionary<string, GameOverride>? overrides,
        bool force,
        int requestedConcurrency,
        long generation,
        CancellationToken cancellationToken)
    {
        var result = new InstalledGame[games.Count];
        var analyses = new PersistedGameAnalysis?[games.Count];
        var analyzedCount = 0;
        var cacheHits = 0;
        var traversals = 0;
        var concurrency = requestedConcurrency > 0
            ? requestedConcurrency
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = games.Select(async (game, index) =>
        {
            GameOverride? manualOverride = null;
            overrides?.TryGetValue(game.EffectiveInstallId, out manualOverride);
            var fingerprint = BuildAnalysisInputFingerprint(game, manualOverride);
            if (!force && previous.TryGetValue(game.InstallId, out var cached) &&
                cached.AnalyzerVersion == GameAnalyzerVersions.SchemaVersion &&
                cached.InputFingerprint == fingerprint)
            {
                Interlocked.Increment(ref cacheHits);
                result[index] = ApplyCachedAnalysis(game, cached);
                analyses[index] = cached;
                return;
            }

            if (!ShouldAnalyze(game))
            {
                result[index] = game;
                analyses[index] = CreatePersistedAnalysis(game, fingerprint, TimeSpan.Zero, generation);
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var started = Stopwatch.StartNew();
            try
            {
                var analysis = await analyzer.AnalyzeAsync(
                    new(
                        game.Store.ToString().ToLowerInvariant(),
                        game.ExternalId ?? game.InstallId.Value,
                        game.Name,
                        game.CanonicalInstallRoot),
                    new GameAnalysisOptions(),
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref analyzedCount);
                Interlocked.Add(ref traversals, analysis.Fingerprint.DirectoriesVisited);
                var candidates = analysis.Fingerprint.Executables
                    .Select(item => new ExecutableCandidate(
                        item.Path, item.Score, item.Confidence, item.Architecture, item.Size, item.Reasons))
                    .ToArray();
                var executable = game.Executable;
                if ((string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) &&
                    analysis.PrimaryExecutable is not null)
                    executable = analysis.PrimaryExecutable.Path;
                var deployment = executable is null
                    ? game.DeploymentDirectory ?? game.CanonicalInstallRoot
                    : Path.GetDirectoryName(executable) ?? game.CanonicalInstallRoot;
                var analyzedGame = game with
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
                result[index] = analyzedGame;
                started.Stop();
                analyses[index] = CreatePersistedAnalysis(analyzedGame, fingerprint, started.Elapsed, generation);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new(
            result.ToList(),
            analyses.Where(item => item is not null).Select(item => item!).ToList(),
            analyzedCount,
            traversals,
            cacheHits);
    }

    public static string BuildAnalysisInputFingerprint(InstalledGame game, GameOverride? manualOverride = null)
    {
        var builder = new StringBuilder();
        builder.Append(GameAnalyzerVersions.SchemaVersion).Append('|')
            .Append(GameIdentity.NormalizePath(game.CanonicalInstallRoot)).Append('|')
            .Append(GameIdentity.NormalizePath(game.Executable ?? string.Empty)).Append('|')
            .Append(GameIdentity.NormalizePath(game.DeploymentDirectory ?? string.Empty)).Append('|')
            .Append(manualOverride?.Executable ?? string.Empty).Append('|')
            .Append(manualOverride?.DeploymentDirectory ?? string.Empty).Append('|')
            .Append(manualOverride?.Prefix ?? string.Empty).Append('|')
            .Append(game.Sources.FirstOrDefault()?.ExecutableHint ?? string.Empty).Append('|');
        AppendFileMetadata(builder, game.Executable);
        foreach (var marker in EnumerateTopLevelMarkers(game.CanonicalInstallRoot))
            AppendFileMetadata(builder, marker);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static RecordDelta CompareRecords(
        IReadOnlyList<SourceGameRecord> previous,
        IReadOnlyList<SourceGameRecord> current)
    {
        var old = previous.ToDictionary(SourceRecordKey, StringComparer.Ordinal);
        var next = current.ToDictionary(SourceRecordKey, StringComparer.Ordinal);
        var added = next.Keys.Except(old.Keys).ToHashSet(StringComparer.Ordinal);
        var removed = old.Keys.Except(next.Keys).ToHashSet(StringComparer.Ordinal);
        var changed = next.Keys
            .Intersect(old.Keys)
            .Where(key => SourceRecordFingerprint(next[key]) != SourceRecordFingerprint(old[key]))
            .ToHashSet(StringComparer.Ordinal);
        return new(added, changed, removed);
    }

    private static List<SourceGameRecord> MergeRecords(
        IReadOnlyList<SourceGameRecord> previous,
        IReadOnlyList<GameSourceScanResult> results,
        LibraryRefreshScope scope)
    {
        var records = previous.ToDictionary(SourceRecordKey, StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (result.FromCache) continue;
            var targetedDocuments = TargetDocuments(scope, result.Root);
            if (targetedDocuments is { Count: > 0 })
            {
                foreach (var old in records.Values.Where(record =>
                             record.ProviderId == result.ProviderId &&
                             (targetedDocuments.Contains(GameIdentity.NormalizePath(record.MetadataPath)) ||
                              record.ConfigurationPath is not null &&
                              targetedDocuments.Contains(GameIdentity.NormalizePath(record.ConfigurationPath)))).ToArray())
                    records.Remove(SourceRecordKey(old));

                foreach (var record in result.Games.Where(record =>
                             targetedDocuments.Contains(GameIdentity.NormalizePath(record.MetadataPath)) ||
                             record.ConfigurationPath is not null &&
                             targetedDocuments.Contains(GameIdentity.NormalizePath(record.ConfigurationPath))))
                    records[SourceRecordKey(record)] = record;
            }
            else
            {
                foreach (var old in records.Values.Where(record =>
                             record.ProviderId == result.ProviderId && MatchesRoot(record, result.Root)).ToArray())
                    records.Remove(SourceRecordKey(old));
                foreach (var record in result.Games)
                    records[SourceRecordKey(record)] = record;
            }
        }
        return records.Values.OrderBy(SourceRecordKey, StringComparer.Ordinal).ToList();
    }

    private static IncrementalReconciliationResult ReconcileIncrementally(
        IReadOnlyList<InstalledGame> previousGames,
        IReadOnlyList<SourceGameRecord> previousRecords,
        IReadOnlyList<SourceGameRecord> records,
        RecordDelta delta,
        bool full)
    {
        if (full)
        {
            var complete = GameReconciliation.Reconcile(records);
            return new(
                complete.Games,
                complete.Diagnostics,
                complete.DuplicateSourceCount,
                complete.DuplicatePhysicalInstallCount,
                complete.Games.Count);
        }
        if (delta.TotalChanges == 0)
            return new(previousGames, [], 0, 0, 0);

        var affectedRecordKeys = delta.Added.Concat(delta.Changed).Concat(delta.Removed)
            .ToHashSet(StringComparer.Ordinal);
        var oldByKey = previousRecords.ToDictionary(SourceRecordKey, StringComparer.Ordinal);
        var newByKey = records.ToDictionary(SourceRecordKey, StringComparer.Ordinal);
        var physicalKeys = affectedRecordKeys
            .SelectMany(key => new[] { oldByKey.GetValueOrDefault(key), newByKey.GetValueOrDefault(key) })
            .Where(record => record is not null)
            .Select(record => GameIdentity.PhysicalKey(record!.InstallRoot, record.ExecutableHint))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var affectedInstallIds = previousGames
            .Where(game => game.Sources.Any(source => affectedRecordKeys.Contains(SourceRecordKey(source))) ||
                           physicalKeys.Contains(GameIdentity.PhysicalKey(game.CanonicalInstallRoot, game.Executable)))
            .Select(game => game.InstallId)
            .ToHashSet();

        var cluster = records.Where(record =>
                affectedRecordKeys.Contains(SourceRecordKey(record)) ||
                physicalKeys.Contains(GameIdentity.PhysicalKey(record.InstallRoot, record.ExecutableHint)))
            .ToArray();
        var partial = GameReconciliation.Reconcile(cluster);
        var retained = previousGames.Where(game => !affectedInstallIds.Contains(game.InstallId)).ToList();
        retained.AddRange(partial.Games);
        return new(
            retained.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.InstallId).ToArray(),
            partial.Diagnostics,
            partial.DuplicateSourceCount,
            partial.DuplicatePhysicalInstallCount,
            Math.Max(partial.Games.Count, affectedInstallIds.Count));
    }

    private static List<SourceDocumentEntry> BuildDocuments(
        IReadOnlyList<SourceDocumentEntry> previous,
        IReadOnlyList<GameSourceScanResult> results,
        LibraryRefreshScope scope,
        long generation)
    {
        var documents = previous.ToDictionary(document => document.DocumentId, StringComparer.Ordinal);
        foreach (var result in results)
        {
            var targeted = TargetDocuments(scope, result.Root);
            if (targeted is { Count: > 0 })
            {
                foreach (var existing in documents.Values.Where(document =>
                             document.ProviderId == result.ProviderId && targeted.Contains(document.CanonicalPath)).ToArray())
                    documents.Remove(existing.DocumentId);
                foreach (var path in targeted.Where(path => File.Exists(path) || Directory.Exists(path)))
                {
                    var document = SourceDocumentEntry.FromPath(
                        result.ProviderId, result.Root.CanonicalPath, path, 1, generation);
                    documents[document.DocumentId] = document;
                }
            }
            else if (!result.FromCache)
            {
                foreach (var existing in documents.Values.Where(document =>
                             document.ProviderId == result.ProviderId && document.RootId == result.Root.CanonicalPath).ToArray())
                    documents.Remove(existing.DocumentId);
                foreach (var path in result.MetadataFiles.Distinct(StringComparer.Ordinal))
                {
                    var document = SourceDocumentEntry.FromPath(
                        result.ProviderId, result.Root.CanonicalPath, path, 1, generation);
                    documents[document.DocumentId] = document;
                }
            }
        }
        return documents.Values.OrderBy(document => document.DocumentId, StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<InstalledGame> RehydrateGames(
        IReadOnlyList<InstalledGame> games,
        IReadOnlyList<SourceGameRecord> records,
        IReadOnlyList<GameSourceRelationship> relationships)
    {
        var sourceByKey = records.ToDictionary(SourceRecordKey, StringComparer.Ordinal);
        var relationshipsByGame = relationships.GroupBy(item => item.InstallId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SourcePriority).ToArray());
        return games.Select(game =>
        {
            if (!relationshipsByGame.TryGetValue(game.InstallId, out var related)) return game;
            var sources = related.Select(item => sourceByKey.GetValueOrDefault(
                    SourceRecordKey(item.ProviderId, item.ExternalId, item.MetadataPath)))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            return game with
            {
                Sources = sources,
                FieldSelections = related.SelectMany(item => item.SelectedFieldEvidence).Distinct().ToArray()
            };
        }).ToArray();
    }

    private static IReadOnlyList<GameSourceRelationship> BuildRelationships(
        IReadOnlyList<InstalledGame> games,
        long generation) => games.SelectMany(game => game.Sources.Select((source, index) => new GameSourceRelationship(
            game.InstallId,
            source.ProviderId,
            source.ExternalId,
            source.MetadataPath,
            index,
            game.Confidence,
            game.FieldSelections ?? [],
            generation))).ToArray();

    private static IReadOnlyList<LibraryProviderStatus> BuildProviderStatuses(
        IReadOnlyList<IGameSourceProvider> considered,
        IReadOnlyDictionary<string, IReadOnlyList<GameSourceRoot>> roots,
        IReadOnlyList<GameSourceScanResult> results,
        IReadOnlyList<SourceGameRecord> records) => considered.Select(provider =>
    {
        var providerRoots = roots.GetValueOrDefault(provider.Id) ?? [];
        var providerResults = results.Where(result => result.ProviderId == provider.Id).ToArray();
        var stale = records.Any(record => record.ProviderId == provider.Id && record.Attributes.GetValueOrDefault("stale") == "true");
        var available = providerRoots.Any(root => root.Exists && root.Readable && !root.Deduplicated);
        var recordCount = records.Count(record => record.ProviderId == provider.Id);
        var summary = !available
            ? "No readable source root"
            : providerResults.Any(result => result.Diagnostics.Any(diagnostic => diagnostic.Severity == SourceDiagnosticSeverity.Error))
                ? "Refresh failed"
                : stale ? "Using stale metadata" : $"{recordCount} records";
        return new LibraryProviderStatus(
            provider.Id, provider.DisplayName, true, available, stale,
            providerRoots.Count(root => !root.Deduplicated), recordCount, summary);
    }).ToArray();

    private static PersistedGameAnalysis CreatePersistedAnalysis(
        InstalledGame game,
        string inputFingerprint,
        TimeSpan duration,
        long generation) => new(
        game.InstallId,
        GameAnalyzerVersions.SchemaVersion,
        inputFingerprint,
        game.Executable,
        game.CandidateList,
        game.Engine,
        game.Fingerprint,
        game.Fingerprint?.FilesVisited ?? 0,
        game.Fingerprint?.DirectoriesVisited ?? 0,
        (long)duration.TotalMilliseconds,
        game,
        generation);

    private static InstalledGame ApplyCachedAnalysis(InstalledGame current, PersistedGameAnalysis cached) => current with
    {
        Executable = cached.SelectedExecutable ?? current.Executable,
        DeploymentDirectory = cached.Game.DeploymentDirectory ?? current.DeploymentDirectory,
        Fingerprint = cached.Fingerprint,
        Engine = cached.Engine,
        Candidates = cached.CandidateExecutables,
        Confidence = cached.Game.Confidence,
        SelectionReason = cached.Game.SelectionReason,
        RequiresConfirmation = current.RequiresConfirmation || cached.Game.RequiresConfirmation
    };

    private static bool ShouldAnalyze(InstalledGame game) =>
        game.IsActionable && game.Platform == GameBinaryPlatform.Windows &&
        !string.IsNullOrWhiteSpace(game.CanonicalInstallRoot) && Directory.Exists(game.CanonicalInstallRoot) &&
        game.PrimaryLauncher != GameLauncher.Steam;

    private static void AppendFileMetadata(StringBuilder builder, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var info = new FileInfo(path);
            builder.Append(GameIdentity.NormalizePath(path)).Append(':')
                .Append(info.Exists ? info.Length : -1).Append(':')
                .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('|');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            builder.Append(GameIdentity.NormalizePath(path)).Append(":unreadable|");
        }
    }

    private static IEnumerable<string> EnumerateTopLevelMarkers(string root)
    {
        if (!Directory.Exists(root)) yield break;
        string[] files;
        try
        {
            files = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files.Where(path =>
                     Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(path).Equals(".addon64", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(path).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(path).Equals("dxgi.ini", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(path => path, StringComparer.Ordinal))
            yield return file;
    }

    private static int CountParsedDocuments(
        IReadOnlyList<GameSourceScanResult> results,
        LibraryRefreshScope scope) => results.Where(result => !result.FromCache && result.SourceChanged)
        .Sum(result => result.MetadataFiles.Count);

    private static int CountStattedDocuments(
        IReadOnlyList<GameSourceScanResult> results,
        LibraryRefreshScope scope) => results.Sum(result => scope.Documents is { Count: > 0 }
        ? TargetDocuments(scope, result.Root)?.Count ?? 0
        : result.MetadataFiles.Count);

    private static (int Added, int Updated, int Removed) CompareGames(
        IReadOnlyList<InstalledGame> previous,
        IReadOnlyList<InstalledGame> current)
    {
        var old = previous.ToDictionary(game => game.InstallId, InstalledGameFingerprint);
        var next = current.ToDictionary(game => game.InstallId, InstalledGameFingerprint);
        return (
            next.Keys.Except(old.Keys).Count(),
            next.Count(pair => old.TryGetValue(pair.Key, out var fingerprint) && fingerprint != pair.Value),
            old.Keys.Except(next.Keys).Count());
    }

    private static string InstalledGameFingerprint(InstalledGame game) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(game))).ToLowerInvariant();

    private static string ResolveDatabasePath(MultiSourceScanRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.LibraryDatabasePath)) return request.LibraryDatabasePath!;
        if (!string.IsNullOrWhiteSpace(request.SourceIndexPath))
        {
            var directory = Path.GetDirectoryName(request.SourceIndexPath!) ?? ".";
            return Path.Combine(directory, "library.db");
        }
        return new XdgPaths().LibraryDatabaseFile;
    }

    private static string SourceRecordKey(SourceGameRecord record) =>
        SourceRecordKey(record.ProviderId, record.ExternalId, record.MetadataPath);

    private static string SourceRecordKey(string providerId, string externalId, string metadataPath) =>
        $"{providerId}|{externalId}|{GameIdentity.NormalizePath(metadataPath)}";

    private static string SourceRecordFingerprint(SourceGameRecord record) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record))).ToLowerInvariant();

    private static bool IsPathForRoot(string path, GameSourceRoot root)
    {
        var normalized = GameIdentity.NormalizePath(path);
        return normalized.Equals(root.CanonicalPath, StringComparison.Ordinal) ||
               normalized.StartsWith(root.CanonicalPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               root.Attributes?.Values.Any(value => !string.IsNullOrWhiteSpace(value) &&
                   (normalized.Equals(GameIdentity.NormalizePath(value), StringComparison.Ordinal) ||
                   normalized.StartsWith(GameIdentity.NormalizePath(value) + Path.DirectorySeparatorChar, StringComparison.Ordinal))) == true;
    }

    private static IReadOnlySet<string>? TargetDocuments(LibraryRefreshScope scope, GameSourceRoot root)
    {
        if (scope.Documents is not { Count: > 0 }) return null;
        var explicitlyTargeted = scope.RootIds?.Contains(root.CanonicalPath) == true;
        return scope.Documents
            .Where(path => explicitlyTargeted || IsPathForRoot(path, root))
            .Select(GameIdentity.NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool MatchesRoot(SourceGameRecord record, GameSourceRoot root)
    {
        if (string.IsNullOrWhiteSpace(record.MetadataPath)) return false;
        var metadata = GameIdentity.NormalizePath(record.MetadataPath);
        return metadata.StartsWith(root.CanonicalPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               string.Equals(metadata, root.CanonicalPath, StringComparison.Ordinal) ||
               string.Equals(Path.GetDirectoryName(metadata), root.CanonicalPath, StringComparison.Ordinal);
    }

    private static List<InstalledGame> MarkStaleFromAttributes(IReadOnlyList<InstalledGame> games) => games
        .Select(game => game.IsStale || game.Sources.Any(source =>
            source.Attributes.GetValueOrDefault("stale") == "true")
            ? game with { IsStale = true }
            : game)
        .ToList();

    private static List<InstalledGame> EnrichFromSourceAttributes(IReadOnlyList<InstalledGame> games)
    {
        return games.Select(game =>
        {
            var primary = game.Sources.FirstOrDefault(source => source.Launcher == game.PrimaryLauncher)
                          ?? game.Sources.FirstOrDefault();
            if (primary is null) return game;
            var engine = game.Engine;
            if (engine == GameEngine.Unknown && primary.Attributes.TryGetValue("engine", out var engineRaw) &&
                Enum.TryParse<GameEngine>(engineRaw, true, out var parsedEngine)) engine = parsedEngine;
            var confidence = game.Confidence;
            if (primary.Attributes.TryGetValue("confidence", out var confidenceRaw) &&
                Enum.TryParse<DetectionConfidence>(confidenceRaw, true, out var parsedConfidence)) confidence = parsedConfidence;
            var reason = primary.Attributes.GetValueOrDefault("selectionReason") ?? game.SelectionReason;
            var deployment = primary.Attributes.GetValueOrDefault("deploymentDirectory") ?? game.DeploymentDirectory;
            return game with { Engine = engine, Confidence = confidence, SelectionReason = reason, DeploymentDirectory = deployment };
        }).ToList();
    }

    private sealed record ProviderScanBatch(IReadOnlyList<GameSourceScanResult> Results, TimeSpan Elapsed);
    private sealed record RecordDelta(
        IReadOnlySet<string> Added,
        IReadOnlySet<string> Changed,
        IReadOnlySet<string> Removed)
    {
        public int TotalChanges => Added.Count + Changed.Count + Removed.Count;
    }
    private sealed record IncrementalReconciliationResult(
        IReadOnlyList<InstalledGame> Games,
        IReadOnlyList<SourceDiagnostic> Diagnostics,
        int DuplicateSourceCount,
        int DuplicatePhysicalInstallCount,
        int InstallationsReconciled);
    private sealed record AnalysisResult(
        List<InstalledGame> Games,
        List<PersistedGameAnalysis> Analyses,
        int AnalyzedCount,
        int DirectoryTraversals,
        int CacheHits = 0);
}
