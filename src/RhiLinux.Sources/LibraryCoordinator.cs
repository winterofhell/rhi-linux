using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class LibraryCoordinator : ILibraryCoordinator
{
    private readonly MultiSourceLibraryService libraryService;
    private readonly IReadOnlyList<IGameSourceProvider> providers;
    private readonly string databasePath;
    private readonly string? legacySourceIndexPath;
    private readonly TimeSpan coalesceDelay;
    private readonly object queueGate = new();
    private readonly object snapshotGate = new();
    private readonly List<RefreshRequest> pending = [];
    private readonly List<SourceDiagnostic> watcherDiagnostics = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SourceWatcherCoordinator watcher;
    private Task processorTask = Task.CompletedTask;
    private LibrarySnapshot current = LibrarySnapshot.Empty;
    private GameSourceDiscoveryContext discoveryContext;
    private IReadOnlySet<string>? enabledProviders;
    private IReadOnlyDictionary<string, GameOverride> overrides = new Dictionary<string, GameOverride>();
    private bool processing;
    private bool watchingEnabled = true;
    private bool disposed;

    public LibraryCoordinator(
        MultiSourceLibraryService libraryService,
        IReadOnlyList<IGameSourceProvider> providers,
        GameSourceDiscoveryContext discoveryContext,
        string databasePath,
        string? legacySourceIndexPath = null,
        IReadOnlySet<string>? enabledProviders = null,
        TimeSpan? coalesceDelay = null,
        TimeSpan? watcherDebounce = null)
    {
        this.libraryService = libraryService;
        this.providers = providers;
        this.discoveryContext = discoveryContext;
        this.databasePath = databasePath;
        this.legacySourceIndexPath = legacySourceIndexPath;
        this.enabledProviders = enabledProviders;
        this.coalesceDelay = coalesceDelay ?? TimeSpan.FromMilliseconds(75);
        watcher = new SourceWatcherCoordinator(HandleWatchEventsAsync, watcherDebounce, RecordWatcherDiagnostic);
    }

    public LibrarySnapshot Current
    {
        get
        {
            lock (snapshotGate) return current;
        }
    }

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public void Configure(
        GameSourceDiscoveryContext context,
        IReadOnlySet<string>? enabled,
        IReadOnlyDictionary<string, GameOverride>? gameOverrides = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        discoveryContext = context;
        enabledProviders = enabled;
        overrides = gameOverrides ?? new Dictionary<string, GameOverride>();
    }

    public void SetWatchingEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        watchingEnabled = enabled;
        if (!enabled) watcher.ReplaceTargets([]);
    }

    public async Task<LibrarySnapshot> LoadCachedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await using var index = new SqliteLibraryIndex(databasePath);
        try
        {
            await index.OpenAsync(cancellationToken).ConfigureAwait(false);
            await index.QuarantineIfCorruptAsync(cancellationToken).ConfigureAwait(false);
            var games = await index.LoadCachedGamesAsync(cancellationToken).ConfigureAwait(false);
            var diagnostics = (await index.LoadDiagnosticsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (index.RebuiltCorruptDatabase)
                diagnostics.Add(new("multi-source", SourceDiagnosticCodes.SourceDatabaseReadFailed,
                    SourceDiagnosticSeverity.Warning,
                    "Library cache was rebuilt. Your game files, profiles, backups, and ownership state were not modified.",
                    index.QuarantinedDatabasePath));
            var generation = await index.GetActiveGenerationAsync(cancellationToken).ConfigureAwait(false);
            var statuses = games.GroupBy(game => game.Sources.FirstOrDefault()?.ProviderId ?? game.PrimaryLauncher.ToString().ToLowerInvariant())
                .Select(group => new LibraryProviderStatus(
                    group.Key,
                    providers.FirstOrDefault(provider => provider.Id == group.Key)?.DisplayName ?? group.Key,
                    true,
                    true,
                    group.Any(game => game.IsStale),
                    0,
                    group.Count(),
                    $"{group.Count()} cached games"))
                .ToArray();
            var snapshot = LibrarySnapshot.Create(generation, games, diagnostics, statuses, LibraryMetrics.Empty);
            Publish(snapshot);
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            var snapshot = LibrarySnapshot.Create(
                0,
                [],
                [new("multi-source", SourceDiagnosticCodes.SourceDatabaseReadFailed,
                    SourceDiagnosticSeverity.Error, "The cached library could not be loaded.", exception.Message)],
                [],
                LibraryMetrics.Empty);
            Publish(snapshot);
            return snapshot;
        }
    }

    public async Task<LibraryRefreshResult> RefreshAsync(
        LibraryRefreshScope scope,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<LibraryRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (queueGate)
        {
            pending.Add(new(scope, completion, cancellationToken));
            if (!processing)
            {
                processing = true;
                processorTask = ProcessQueueAsync(lifetime.Token);
            }
        }
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryRefreshResult> RebuildCacheAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SetWatchingEnabled(false);
        try
        {
            Task active;
            lock (queueGate) active = processorTask;
            await active.WaitAsync(cancellationToken).ConfigureAwait(false);
            DeleteCacheFile(databasePath);
            DeleteCacheFile(databasePath + "-wal");
            DeleteCacheFile(databasePath + "-shm");
            return await RefreshAsync(LibraryRefreshScope.Full("Library cache rebuilt", true), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            SetWatchingEnabled(true);
            await RefreshWatchTargetsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void DeleteCacheFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(coalesceDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            RefreshRequest[] batch;
            lock (queueGate)
            {
                batch = pending.ToArray();
                pending.Clear();
            }
            if (batch.Length == 0)
            {
                lock (queueGate)
                {
                    if (pending.Count == 0)
                    {
                        processing = false;
                        return;
                    }
                }
                continue;
            }

            var cancelled = batch.Where(request => request.CancellationToken.IsCancellationRequested).ToArray();
            foreach (var request in cancelled)
                request.Completion.TrySetCanceled(request.CancellationToken);
            batch = batch.Where(request => !request.CancellationToken.IsCancellationRequested).ToArray();
            if (batch.Length == 0) continue;

            var scope = batch.Select(request => request.Scope).Aggregate((left, right) => left.Merge(right));
            using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var cancellationGate = new object();
            var remainingRequests = batch.Length;
            var registrations = batch.Select(request => request.CancellationToken.Register(() =>
            {
                lock (cancellationGate)
                {
                    remainingRequests--;
                    if (remainingRequests == 0) batchCancellation.Cancel();
                }
            })).ToArray();
            try
            {
                var result = await ExecuteRefreshAsync(scope, batchCancellation.Token).ConfigureAwait(false);
                foreach (var request in batch)
                {
                    if (request.CancellationToken.IsCancellationRequested)
                        request.Completion.TrySetCanceled(request.CancellationToken);
                    else
                        request.Completion.TrySetResult(result);
                }
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                foreach (var request in batch) request.Completion.TrySetCanceled(exception.CancellationToken);
                break;
            }
            catch (OperationCanceledException)
            {
                foreach (var request in batch)
                    request.Completion.TrySetCanceled(request.CancellationToken.IsCancellationRequested
                        ? request.CancellationToken
                        : batchCancellation.Token);
            }
            catch (Exception exception)
            {
                foreach (var request in batch) request.Completion.TrySetException(exception);
            }
            finally
            {
                foreach (var registration in registrations) registration.Dispose();
            }
        }

        RefreshRequest[] remaining;
        lock (queueGate)
        {
            remaining = pending.ToArray();
            pending.Clear();
            processing = false;
        }
        foreach (var request in remaining) request.Completion.TrySetCanceled(cancellationToken);
    }

    private async Task<LibraryRefreshResult> ExecuteRefreshAsync(
        LibraryRefreshScope scope,
        CancellationToken cancellationToken)
    {
        var result = await libraryService.ScanAsync(
            new(
                DiscoveryContext: discoveryContext,
                EnabledProviders: enabledProviders,
                ForceFullScan: scope.Force,
                SourceIndexPath: legacySourceIndexPath,
                LibraryDatabasePath: databasePath,
                RefreshScope: scope,
                Overrides: overrides),
            cancellationToken).ConfigureAwait(false);
        SourceDiagnostic[] watcherItems;
        lock (watcherDiagnostics)
        {
            watcherItems = watcherDiagnostics.ToArray();
            watcherDiagnostics.Clear();
        }
        var previous = Current;
        var executedProviders = (result.Metrics?.ProvidersExecuted ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var diagnostics = previous.Diagnostics
            .Where(item => !executedProviders.Contains(item.ProviderId))
            .Concat(result.Diagnostics)
            .Concat(watcherItems)
            .ToArray();
        var refreshedProviders = (result.ProviderStatuses ?? [])
            .Where(item => executedProviders.Contains(item.ProviderId))
            .ToDictionary(item => item.ProviderId, StringComparer.Ordinal);
        var priorAndRefreshed = previous.Providers
            .Where(item => !executedProviders.Contains(item.ProviderId))
            .Concat(refreshedProviders.Values)
            .GroupBy(item => item.ProviderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var providerStatuses = providers.Select(provider =>
        {
            var enabled = enabledProviders is null || enabledProviders.Count == 0 || enabledProviders.Contains(provider.Id);
            if (priorAndRefreshed.TryGetValue(provider.Id, out var status))
                return status with
                {
                    Enabled = enabled,
                    Summary = enabled ? status.Summary : "Disabled"
                };
            return new LibraryProviderStatus(
                provider.Id,
                provider.DisplayName,
                enabled,
                false,
                false,
                0,
                0,
                enabled ? "Not refreshed" : "Disabled");
        }).ToArray();
        var visibleGames = result.Games.Where(game => game.Sources.Count == 0 || game.Sources.Any(source =>
            enabledProviders is null || enabledProviders.Count == 0 || enabledProviders.Contains(source.ProviderId))).ToArray();
        var snapshot = LibrarySnapshot.Create(
            result.ScanGeneration,
            ApplyOverrides(visibleGames, overrides),
            diagnostics,
            providerStatuses,
            result.Metrics ?? LibraryMetrics.Empty);
        cancellationToken.ThrowIfCancellationRequested();
        var changes = LibraryChangeSet.Between(previous, snapshot);
        Publish(snapshot);
        await RefreshWatchTargetsAsync(cancellationToken).ConfigureAwait(false);
        return new(snapshot, changes, snapshot.Metrics, true);
    }

    private async Task HandleWatchEventsAsync(
        IReadOnlyList<SourceWatchEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0 || disposed) return;
        var providers = events.Select(item => item.ProviderId).ToHashSet(StringComparer.Ordinal);
        var roots = events.Select(item => item.RootId).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
        var documents = events.SelectMany(EventDocuments)
            .Select(GameIdentity.NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
        var reason = events.Count == 1
            ? $"{events[0].ProviderId} metadata {events[0].ChangeType.ToString().ToLowerInvariant()}"
            : $"{events.Count} source metadata events";
        await RefreshAsync(new(providers, roots, documents, false, reason), cancellationToken)
            .ConfigureAwait(false);
    }

    private static IEnumerable<string> EventDocuments(SourceWatchEvent item)
    {
        foreach (var path in item.OldPath is null ? new[] { item.Path } : new[] { item.OldPath, item.Path })
        {
            yield return path;
            if (item.ProviderId == "bottles" &&
                !Path.GetFileName(path).Equals("bottle.yml", StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(path, "bottle.yml");
        }
    }

    private async Task RefreshWatchTargetsAsync(CancellationToken cancellationToken)
    {
        if (!watchingEnabled)
        {
            watcher.ReplaceTargets([]);
            return;
        }
        var targets = new List<SourceWatchTarget>();
        foreach (var provider in providers.Where(provider =>
                     enabledProviders is null || enabledProviders.Count == 0 || enabledProviders.Contains(provider.Id)))
        {
            IReadOnlyList<GameSourceRoot> roots;
            try
            {
                roots = await provider.DiscoverRootsAsync(discoveryContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RecordWatcherDiagnostic(new(
                    SourceWatcherDiagnosticCode.TargetUnreadable,
                    SourceDiagnosticSeverity.Warning,
                    provider.Id,
                    string.Empty,
                    string.Empty,
                    exception.Message,
                    DateTimeOffset.UtcNow,
                    ExceptionType: exception.GetType().Name));
                continue;
            }

            foreach (var root in roots.Where(item => item.Exists && item.Readable && !item.Deduplicated))
            {
                targets.AddRange(provider.Id switch
                {
                    "steam" => SourceWatchTargets.ForSteam(root),
                    "heroic" => SourceWatchTargets.ForHeroic(root),
                    "legendary" => SourceWatchTargets.ForLegendary(root),
                    "lutris" => SourceWatchTargets.ForLutris(root, root.Attributes?.GetValueOrDefault("gamesYamlRoot")),
                    "bottles" => SourceWatchTargets.ForBottles(root),
                    "minigalaxy" => SourceWatchTargets.ForMinigalaxy(root),
                    "manual" => SourceWatchTargets.ForManual(root),
                    _ => []
                });
            }
        }
        watcher.ReplaceTargets(targets);
    }

    private void RecordWatcherDiagnostic(SourceWatcherDiagnostic diagnostic)
    {
        lock (watcherDiagnostics)
        {
            watcherDiagnostics.Add(new(
                diagnostic.ProviderId,
                diagnostic.Code.ToString(),
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.ExceptionType is null
                    ? $"Attempt {diagnostic.Attempt}"
                    : $"{diagnostic.ExceptionType}; attempt {diagnostic.Attempt}",
                diagnostic.Path));
            if (watcherDiagnostics.Count > 100) watcherDiagnostics.RemoveAt(0);
        }
    }

    private void Publish(LibrarySnapshot snapshot)
    {
        LibrarySnapshot previous;
        lock (snapshotGate)
        {
            previous = current;
            current = snapshot;
        }
        var changes = LibraryChangeSet.Between(previous, snapshot);
        if (!changes.IsEmpty || previous.Generation != snapshot.Generation)
            Changed?.Invoke(this, new(previous, snapshot, changes));
    }

    private static IReadOnlyList<InstalledGame> ApplyOverrides(
        IReadOnlyList<InstalledGame> games,
        IReadOnlyDictionary<string, GameOverride> gameOverrides) => games.Select(game =>
    {
        if (!gameOverrides.TryGetValue(game.EffectiveInstallId, out var gameOverride) &&
            !(game.SteamAppId is { } steamAppId &&
              (gameOverrides.TryGetValue(steamAppId.ToString(), out gameOverride) ||
               gameOverrides.TryGetValue(GameInstallId.LegacySteam(steamAppId).Value, out gameOverride))))
            return game;
        var executable = gameOverride.Executable ?? game.Executable;
        var deployment = gameOverride.DeploymentDirectory ??
                         (executable is null ? game.DeploymentDirectory : Path.GetDirectoryName(executable));
        return game with
        {
            Executable = executable,
            DeploymentDirectory = deployment ?? game.DeploymentDirectory,
            Prefix = gameOverride.Prefix ?? game.Prefix,
            Confidence = gameOverride.Executable is null ? game.Confidence : DetectionConfidence.High,
            SelectionReason = gameOverride.Executable is null
                ? game.SelectionReason
                : "Selected because a persistent manual executable override is configured."
        };
    }).ToArray();

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        await watcher.DisposeAsync().ConfigureAwait(false);
        try
        {
            await processorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        Changed = null;
        lifetime.Dispose();
    }

    private sealed record RefreshRequest(
        LibraryRefreshScope Scope,
        TaskCompletionSource<LibraryRefreshResult> Completion,
        CancellationToken CancellationToken);
}
