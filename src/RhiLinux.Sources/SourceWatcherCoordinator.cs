using System.Collections.Concurrent;
using RhiLinux.Core;
using RhiLinux.Steam;

namespace RhiLinux.Sources;

public enum SourceWatchTargetKind
{
    File,
    Directory,
    Pattern
}

public sealed record SourceWatchTarget(
    string ProviderId,
    string RootId,
    string Directory,
    string Filter,
    SourceWatchTargetKind Kind,
    bool Recursive = false);

public sealed record SourceWatchEvent(
    string ProviderId,
    string RootId,
    string Path,
    WatcherChangeTypes ChangeType,
    DateTimeOffset TimestampUtc,
    string? OldPath = null);

public enum SourceWatcherDiagnosticCode
{
    TargetMissing,
    TargetUnreadable,
    WatcherCreationFailed,
    WatcherOverflow,
    WatcherRecreated,
    WatcherRecreationExhausted,
    CallbackFailed
}

public sealed record SourceWatcherDiagnostic(
    SourceWatcherDiagnosticCode Code,
    SourceDiagnosticSeverity Severity,
    string ProviderId,
    string RootId,
    string Path,
    string Message,
    DateTimeOffset TimestampUtc,
    int Attempt = 0,
    string? ExceptionType = null);

public interface ISourceWatchTargetProvider
{
    IReadOnlyList<SourceWatchTarget> GetWatchTargets(GameSourceRoot root);
}

public sealed class SourceWatcherCoordinator : IAsyncDisposable
{
    private const int MaximumRecreationAttempts = 5;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    private readonly ConcurrentDictionary<string, FileSystemWatcher> watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SourceWatchTarget> desiredTargets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingSourceWatchEvent> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> recreationTasks = new(StringComparer.Ordinal);
    private readonly object debounceGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan debounce;
    private readonly Func<IReadOnlyList<SourceWatchEvent>, CancellationToken, Task> onEvents;
    private readonly Action<SourceWatcherDiagnostic>? onDiagnostic;
    private CancellationTokenSource? debounceCancellation;
    private Task debounceTask = Task.CompletedTask;
    private bool disposed;

    public SourceWatcherCoordinator(
        Action<IReadOnlyList<SourceWatchEvent>> onEvents,
        TimeSpan? debounce = null,
        Action<SourceWatcherDiagnostic>? onDiagnostic = null)
        : this((events, _) =>
        {
            onEvents(events);
            return Task.CompletedTask;
        }, debounce, onDiagnostic)
    {
    }

    public SourceWatcherCoordinator(
        Func<IReadOnlyList<SourceWatchEvent>, CancellationToken, Task> onEvents,
        TimeSpan? debounce = null,
        Action<SourceWatcherDiagnostic>? onDiagnostic = null)
    {
        this.onEvents = onEvents;
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(400);
        this.onDiagnostic = onDiagnostic;
    }

    public int ActiveWatcherCount => watchers.Count;
    public int DesiredTargetCount => desiredTargets.Count;

    internal bool IsWatching(SourceWatchTarget target) => watchers.ContainsKey(Key(target));

    internal void HandleWatcherFailure(SourceWatchTarget target, Exception? exception = null) =>
        HandleWatcherError(Key(target), target, exception);

    public void ReplaceTargets(IEnumerable<SourceWatchTarget> targets)
    {
        ThrowIfDisposed();
        var desired = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Directory))
            .GroupBy(Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(Key, StringComparer.Ordinal);

        foreach (var existing in desiredTargets.Keys.ToArray())
        {
            if (desired.ContainsKey(existing)) continue;
            desiredTargets.TryRemove(existing, out _);
            RemoveWatcher(existing);
        }

        foreach (var (key, target) in desired)
        {
            desiredTargets[key] = target;
            if (!watchers.ContainsKey(key))
                CreateWatcher(target, scheduleRetry: true);
        }
    }

    public static IReadOnlyList<SourceWatchEvent> Normalize(IEnumerable<SourceWatchEvent> events) =>
        events
            .GroupBy(EventKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.TimestampUtc)
                .Aggregate(MergeEvents))
            .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
            .ThenBy(item => item.RootId, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

    private bool CreateWatcher(SourceWatchTarget target, bool scheduleRetry, int attempt = 0)
    {
        if (disposed) return false;
        var key = Key(target);
        if (!desiredTargets.ContainsKey(key) || watchers.ContainsKey(key)) return true;
        if (!Directory.Exists(target.Directory))
        {
            Report(new(
                SourceWatcherDiagnosticCode.TargetMissing,
                SourceDiagnosticSeverity.Info,
                target.ProviderId,
                target.RootId,
                target.Directory,
                "The metadata directory is not currently available.",
                DateTimeOffset.UtcNow,
                attempt));
            if (scheduleRetry) ScheduleRecreation(key, target, attempt + 1);
            return false;
        }

        try
        {
            var watcher = new FileSystemWatcher(target.Directory)
            {
                Filter = string.IsNullOrWhiteSpace(target.Filter) ? "*" : target.Filter,
                IncludeSubdirectories = target.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 16 * 1024
            };
            watcher.Changed += (_, args) => Enqueue(target, args.FullPath, args.ChangeType);
            watcher.Created += (_, args) => Enqueue(target, args.FullPath, args.ChangeType);
            watcher.Deleted += (_, args) => Enqueue(target, args.FullPath, args.ChangeType);
            watcher.Renamed += (_, args) => Enqueue(target, args.FullPath, args.ChangeType, args.OldFullPath);
            watcher.Error += (_, args) => HandleWatcherError(key, target, args.GetException());
            watcher.EnableRaisingEvents = true;
            if (!watchers.TryAdd(key, watcher))
                DisposeWatcher(watcher);
            else if (attempt > 0)
                Report(new(
                    SourceWatcherDiagnosticCode.WatcherRecreated,
                    SourceDiagnosticSeverity.Info,
                    target.ProviderId,
                    target.RootId,
                    target.Directory,
                    "The metadata watcher was recreated.",
                    DateTimeOffset.UtcNow,
                    attempt));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Report(new(
                exception is UnauthorizedAccessException
                    ? SourceWatcherDiagnosticCode.TargetUnreadable
                    : SourceWatcherDiagnosticCode.WatcherCreationFailed,
                SourceDiagnosticSeverity.Warning,
                target.ProviderId,
                target.RootId,
                target.Directory,
                "The metadata watcher could not be created.",
                DateTimeOffset.UtcNow,
                attempt,
                exception.GetType().Name));
            if (scheduleRetry) ScheduleRecreation(key, target, attempt + 1);
            return false;
        }
    }

    private void HandleWatcherError(string key, SourceWatchTarget target, Exception? exception)
    {
        if (disposed) return;
        Report(new(
            SourceWatcherDiagnosticCode.WatcherOverflow,
            SourceDiagnosticSeverity.Warning,
            target.ProviderId,
            target.RootId,
            target.Directory,
            exception?.Message ?? "The metadata watcher reported an overflow or operating-system error.",
            DateTimeOffset.UtcNow,
            ExceptionType: exception?.GetType().Name));
        Enqueue(target, target.Directory, WatcherChangeTypes.All);
        ReplaceWatcher(key, target);
    }

    private void ReplaceWatcher(string key, SourceWatchTarget target)
    {
        RemoveWatcher(key);
        ScheduleRecreation(key, target, 1);
    }

    private void RemoveWatcher(string key)
    {
        if (watchers.TryRemove(key, out var watcher))
            DisposeWatcher(watcher);
    }

    private void ScheduleRecreation(string key, SourceWatchTarget target, int attempt)
    {
        if (disposed || !desiredTargets.ContainsKey(key) || recreationTasks.ContainsKey(key)) return;
        if (attempt > MaximumRecreationAttempts)
        {
            Report(new(
                SourceWatcherDiagnosticCode.WatcherRecreationExhausted,
                SourceDiagnosticSeverity.Error,
                target.ProviderId,
                target.RootId,
                target.Directory,
                $"The metadata watcher could not be recreated after {MaximumRecreationAttempts} attempts.",
                DateTimeOffset.UtcNow,
                MaximumRecreationAttempts));
            return;
        }

        var task = RecreateAfterDelayAsync(key, target, attempt, lifetime.Token);
        recreationTasks.TryAdd(key, task);
    }

    private async Task RecreateAfterDelayAsync(
        string key,
        SourceWatchTarget target,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)], cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || !desiredTargets.ContainsKey(key)) return;
            var created = CreateWatcher(target, scheduleRetry: false, attempt);
            recreationTasks.TryRemove(key, out _);
            if (!created) ScheduleRecreation(key, target, attempt + 1);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            recreationTasks.TryRemove(key, out _);
            if (desiredTargets.TryGetValue(key, out var desiredTarget))
                Report(new(
                    SourceWatcherDiagnosticCode.WatcherCreationFailed,
                    SourceDiagnosticSeverity.Error,
                    desiredTarget.ProviderId,
                    desiredTarget.RootId,
                    desiredTarget.Directory,
                    exception.Message,
                    DateTimeOffset.UtcNow,
                    ExceptionType: exception.GetType().Name));
        }
        finally
        {
            recreationTasks.TryRemove(key, out _);
        }
    }

    private void Enqueue(
        SourceWatchTarget target,
        string path,
        WatcherChangeTypes changeType,
        string? oldPath = null)
    {
        if (disposed) return;
        var next = new PendingSourceWatchEvent(
            target.ProviderId,
            target.RootId,
            path,
            changeType,
            DateTimeOffset.UtcNow,
            oldPath);
        pending.AddOrUpdate(EventKey(next.ToEvent()), next, (_, current) => current.Merge(next));
        ScheduleDebounce();
    }

    private void ScheduleDebounce()
    {
        lock (debounceGate)
        {
            debounceCancellation?.Cancel();
            debounceCancellation?.Dispose();
            debounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            debounceTask = FlushAfterDelayAsync(debounceCancellation.Token);
        }
    }

    private async Task FlushAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(debounce, cancellationToken).ConfigureAwait(false);
            var snapshot = pending.ToArray();
            foreach (var item in snapshot)
                pending.TryRemove(item.Key, out _);
            if (snapshot.Length == 0 || disposed) return;
            var events = Normalize(snapshot.Select(item => item.Value.ToEvent()));
            try
            {
                await onEvents(events, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Report(new(
                    SourceWatcherDiagnosticCode.CallbackFailed,
                    SourceDiagnosticSeverity.Error,
                    "watcher",
                    string.Empty,
                    string.Empty,
                    exception.Message,
                    DateTimeOffset.UtcNow,
                    ExceptionType: exception.GetType().Name));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Report(SourceWatcherDiagnostic diagnostic)
    {
        try
        {
            onDiagnostic?.Invoke(diagnostic);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                $"Source watcher diagnostic callback failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static SourceWatchEvent MergeEvents(SourceWatchEvent current, SourceWatchEvent next)
    {
        var changeType = MergeChangeTypes(current.ChangeType, next.ChangeType);
        return next with
        {
            ChangeType = changeType,
            OldPath = next.OldPath ?? current.OldPath,
            TimestampUtc = current.TimestampUtc > next.TimestampUtc ? current.TimestampUtc : next.TimestampUtc
        };
    }

    private static WatcherChangeTypes MergeChangeTypes(WatcherChangeTypes current, WatcherChangeTypes next)
    {
        if (current == next) return next;
        if (current == WatcherChangeTypes.Created && next == WatcherChangeTypes.Changed)
            return WatcherChangeTypes.Created;
        if (current == WatcherChangeTypes.Created && next == WatcherChangeTypes.Deleted)
            return WatcherChangeTypes.Deleted;
        if (current == WatcherChangeTypes.Deleted && next == WatcherChangeTypes.Created)
            return WatcherChangeTypes.Changed;
        if (current == WatcherChangeTypes.Renamed || next == WatcherChangeTypes.Renamed)
            return WatcherChangeTypes.Renamed;
        if (current == WatcherChangeTypes.All || next == WatcherChangeTypes.All)
            return WatcherChangeTypes.All;
        return next;
    }

    private static string Key(SourceWatchTarget target) =>
        $"{target.ProviderId}:{target.RootId}:{GameIdentity.NormalizePath(target.Directory)}:{target.Filter}:{target.Recursive}";

    private static string EventKey(SourceWatchEvent item) =>
        $"{item.ProviderId}|{item.RootId}|{GameIdentity.NormalizePath(item.Path)}";

    private static void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        Task debounceToObserve;
        lock (debounceGate)
        {
            debounceCancellation?.Cancel();
            debounceCancellation?.Dispose();
            debounceCancellation = null;
            debounceToObserve = debounceTask;
        }

        foreach (var key in watchers.Keys.ToArray())
            RemoveWatcher(key);

        try
        {
            await Task.WhenAll(recreationTasks.Values.Append(debounceToObserve)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        pending.Clear();
        desiredTargets.Clear();
        recreationTasks.Clear();
        lifetime.Dispose();
    }

    private sealed record PendingSourceWatchEvent(
        string ProviderId,
        string RootId,
        string Path,
        WatcherChangeTypes ChangeType,
        DateTimeOffset TimestampUtc,
        string? OldPath)
    {
        public PendingSourceWatchEvent Merge(PendingSourceWatchEvent next) =>
            FromEvent(MergeEvents(ToEvent(), next.ToEvent()));

        public SourceWatchEvent ToEvent() =>
            new(ProviderId, RootId, Path, ChangeType, TimestampUtc, OldPath);

        private static PendingSourceWatchEvent FromEvent(SourceWatchEvent value) =>
            new(value.ProviderId, value.RootId, value.Path, value.ChangeType, value.TimestampUtc, value.OldPath);
    }
}

public static class SourceWatchTargets
{
    public static IReadOnlyList<SourceWatchTarget> ForSteam(GameSourceRoot root)
    {
        var steamApps = Path.Combine(root.CanonicalPath, "steamapps");
        List<SourceWatchTarget> targets =
        [
            new(SteamGameSourceProvider.ProviderId, root.CanonicalPath, steamApps, "libraryfolders.vdf", SourceWatchTargetKind.File),
            new(SteamGameSourceProvider.ProviderId, root.CanonicalPath, steamApps, "appmanifest_*.acf", SourceWatchTargetKind.Pattern)
        ];
        var libraryFolders = Path.Combine(steamApps, "libraryfolders.vdf");
        try
        {
            if (File.Exists(libraryFolders))
            {
                var document = VdfParser.Parse(File.ReadAllText(libraryFolders));
                foreach (var library in SteamDiscoveryService.EnumerateLibraryPaths(document)
                             .Select(GameIdentity.NormalizePath)
                             .Distinct(StringComparer.Ordinal))
                {
                    var externalSteamApps = Path.Combine(library, "steamapps");
                    if (!externalSteamApps.Equals(steamApps, StringComparison.Ordinal))
                        targets.Add(new(SteamGameSourceProvider.ProviderId, root.CanonicalPath, externalSteamApps,
                            "appmanifest_*.acf", SourceWatchTargetKind.Pattern));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
        }
        return targets;
    }

    public static IReadOnlyList<SourceWatchTarget> ForHeroic(GameSourceRoot root) =>
    [
        new("heroic", root.CanonicalPath, Path.Combine(root.CanonicalPath, "legendaryConfig", "legendary"), "installed.json", SourceWatchTargetKind.File),
        new("heroic", root.CanonicalPath, Path.Combine(root.CanonicalPath, "gog_store"), "installed.json", SourceWatchTargetKind.File),
        new("heroic", root.CanonicalPath, Path.Combine(root.CanonicalPath, "nile_config", "nile"), "installed.json", SourceWatchTargetKind.File),
        new("heroic", root.CanonicalPath, Path.Combine(root.CanonicalPath, "GamesConfig"), "*.json", SourceWatchTargetKind.Pattern)
    ];

    public static IReadOnlyList<SourceWatchTarget> ForLegendary(GameSourceRoot root) =>
    [
        new("legendary", root.CanonicalPath, root.CanonicalPath, "installed.json", SourceWatchTargetKind.File)
    ];

    public static IReadOnlyList<SourceWatchTarget> ForLutris(GameSourceRoot root, string? configGamesDirectory = null)
    {
        var targets = new List<SourceWatchTarget>
        {
            new("lutris", root.CanonicalPath, root.CanonicalPath, "pga.db", SourceWatchTargetKind.File),
            new("lutris", root.CanonicalPath, root.CanonicalPath, "pga.db-wal", SourceWatchTargetKind.File),
            new("lutris", root.CanonicalPath, root.CanonicalPath, "pga.db-shm", SourceWatchTargetKind.File)
        };
        if (!string.IsNullOrWhiteSpace(configGamesDirectory))
            targets.Add(new("lutris", root.CanonicalPath, configGamesDirectory, "*.yml", SourceWatchTargetKind.Pattern));
        return targets;
    }

    public static IReadOnlyList<SourceWatchTarget> ForBottles(GameSourceRoot root)
    {
        var targets = new List<SourceWatchTarget>
        {
            new("bottles", root.CanonicalPath, root.CanonicalPath, "*", SourceWatchTargetKind.Directory)
        };
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root.CanonicalPath)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (Path.GetFileName(directory).Equals("drive_c", StringComparison.OrdinalIgnoreCase)) continue;
                targets.Add(new("bottles", root.CanonicalPath, directory, "bottle.yml", SourceWatchTargetKind.File));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return targets;
    }

    public static IReadOnlyList<SourceWatchTarget> ForMinigalaxy(GameSourceRoot root) =>
    [
        new("minigalaxy", root.CanonicalPath, root.CanonicalPath, "config.json", SourceWatchTargetKind.File),
        new("minigalaxy", root.CanonicalPath, Path.Combine(root.CanonicalPath, "games"), "*.json", SourceWatchTargetKind.Pattern)
    ];

    public static IReadOnlyList<SourceWatchTarget> ForManual(GameSourceRoot root)
    {
        var directory = Path.GetDirectoryName(root.CanonicalPath) ?? root.CanonicalPath;
        var filter = Path.GetFileName(root.CanonicalPath);
        return [new("manual", root.CanonicalPath, directory, filter, SourceWatchTargetKind.File)];
    }
}
