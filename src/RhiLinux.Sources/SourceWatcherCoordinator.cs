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
    SourceWatchTargetKind Kind);

public sealed record SourceWatchEvent(
    string ProviderId,
    string RootId,
    string Path,
    WatcherChangeTypes ChangeType,
    DateTimeOffset TimestampUtc);

public interface ISourceWatchTargetProvider
{
    IReadOnlyList<SourceWatchTarget> GetWatchTargets(GameSourceRoot root);
}

public sealed class SourceWatcherCoordinator : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, FileSystemWatcher> watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private CancellationTokenSource? debounceCts;
    private bool disposed;
    private readonly TimeSpan debounce;
    private readonly Action<IReadOnlyList<SourceWatchEvent>> onEvents;

    public SourceWatcherCoordinator(
        Action<IReadOnlyList<SourceWatchEvent>> onEvents,
        TimeSpan? debounce = null)
    {
        this.onEvents = onEvents;
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(400);
    }

    public int ActiveWatcherCount => watchers.Count;

    public void ReplaceTargets(IEnumerable<SourceWatchTarget> targets)
    {
        ThrowIfDisposed();
        var desired = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Directory) && Directory.Exists(target.Directory))
            .GroupBy(target => $"{target.ProviderId}:{target.RootId}:{target.Directory}:{target.Filter}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var desiredKeys = desired.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var existing in watchers.Keys.ToArray())
        {
            if (desiredKeys.Contains(existing)) continue;
            if (watchers.TryRemove(existing, out var watcher))
                DisposeWatcher(watcher);
        }

        foreach (var target in desired)
        {
            var key = Key(target);
            if (watchers.ContainsKey(key)) continue;
            try
            {
                var watcher = new FileSystemWatcher(target.Directory)
                {
                    Filter = string.IsNullOrWhiteSpace(target.Filter) ? "*.*" : target.Filter,
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };
                watcher.Changed += (_, args) => Enqueue(target, args.FullPath, args.ChangeType);
                watcher.Created += (_, args) => Enqueue(target, args.FullPath, args.ChangeType);
                watcher.Deleted += (_, args) => Enqueue(target, args.FullPath, args.ChangeType);
                watcher.Renamed += (_, args) => Enqueue(target, args.FullPath, args.ChangeType);
                watcher.Error += (_, _) =>
                {
                    Enqueue(target, target.Directory, WatcherChangeTypes.All);
                    RecreateWatcher(key, target);
                };
                watcher.EnableRaisingEvents = true;
                watchers[key] = watcher;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
    }

    public static IReadOnlyList<SourceWatchEvent> Normalize(
        IEnumerable<SourceWatchEvent> events) =>
        events
            .GroupBy(item => $"{item.ProviderId}:{item.RootId}:{item.Path}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.TimestampUtc).First())
            .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
            .ThenBy(item => item.RootId, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

    private void Enqueue(SourceWatchTarget target, string path, WatcherChangeTypes changeType)
    {
        if (disposed) return;
        var key = $"{target.ProviderId}|{target.RootId}|{path}";
        pending[key] = 0;
        lock (gate)
        {
            debounceCts?.Cancel();
            debounceCts?.Dispose();
            debounceCts = new CancellationTokenSource();
            var token = debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(debounce, token).ConfigureAwait(false);
                    Flush();
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
        }
    }

    private void Flush()
    {
        if (disposed) return;
        var snapshot = pending.Keys.ToArray();
        pending.Clear();
        if (snapshot.Length == 0) return;
        var events = snapshot.Select(key =>
        {
            var parts = key.Split('|', 3);
            return new SourceWatchEvent(
                parts[0],
                parts.Length > 1 ? parts[1] : string.Empty,
                parts.Length > 2 ? parts[2] : string.Empty,
                WatcherChangeTypes.Changed,
                DateTimeOffset.UtcNow);
        }).ToArray();
        onEvents(Normalize(events));
    }

    private void RecreateWatcher(string key, SourceWatchTarget target)
    {
        if (watchers.TryRemove(key, out var old))
            DisposeWatcher(old);
        ReplaceTargets([target]);
    }

    private static string Key(SourceWatchTarget target) =>
        $"{target.ProviderId}:{target.RootId}:{target.Directory}:{target.Filter}";

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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        lock (gate)
        {
            debounceCts?.Cancel();
            debounceCts?.Dispose();
            debounceCts = null;
        }

        foreach (var key in watchers.Keys.ToArray())
        {
            if (watchers.TryRemove(key, out var watcher))
                DisposeWatcher(watcher);
        }

        pending.Clear();
        return ValueTask.CompletedTask;
    }
}

public static class SourceWatchTargets
{
    public static IReadOnlyList<SourceWatchTarget> ForSteam(GameSourceRoot root)
    {
        var steamApps = Path.Combine(root.CanonicalPath, "steamapps");
        return
        [
            new(SteamGameSourceProvider.ProviderId, root.CanonicalPath, steamApps, "libraryfolders.vdf", SourceWatchTargetKind.File),
            new(SteamGameSourceProvider.ProviderId, root.CanonicalPath, steamApps, "appmanifest_*.acf", SourceWatchTargetKind.Pattern)
        ];
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
        if (!string.IsNullOrWhiteSpace(configGamesDirectory) && Directory.Exists(configGamesDirectory))
            targets.Add(new("lutris", root.CanonicalPath, configGamesDirectory, "*.yml", SourceWatchTargetKind.Pattern));
        return targets;
    }

    public static IReadOnlyList<SourceWatchTarget> ForBottles(GameSourceRoot root) =>
    [
        new("bottles", root.CanonicalPath, root.CanonicalPath, "bottle.yml", SourceWatchTargetKind.Pattern)
    ];

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
