using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed class SteamLibraryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly object gate = new();
    private CancellationTokenSource? debounce;
    private bool disposed;

    public event EventHandler? LibraryChanged;

    public void Watch(IEnumerable<string> steamRoots)
    {
        Stop();
        foreach (var root in steamRoots.Distinct(StringComparer.Ordinal))
        {
            var steamApps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            try
            {
                var watcher = new FileSystemWatcher(steamApps)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                    Filter = "*",
                    IncludeSubdirectories = false,
                    InternalBufferSize = 64 * 1024
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += (_, _) => Schedule();
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
    }

    public void Stop()
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        watchers.Clear();
        lock (gate)
        {
            debounce?.Cancel();
            debounce?.Dispose();
            debounce = null;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        var name = Path.GetFileName(args.Name ?? args.FullPath);
        if (name is null) return;
        if (!name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("libraryfolders.vdf", StringComparison.OrdinalIgnoreCase))
            return;
        Schedule();
    }

    private void Schedule()
    {
        lock (gate)
        {
            debounce?.Cancel();
            debounce?.Dispose();
            debounce = new CancellationTokenSource();
            var token = debounce.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(750, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                        LibraryChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
    }
}
