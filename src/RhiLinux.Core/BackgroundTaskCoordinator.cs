namespace RhiLinux.Core;

public enum BackgroundTaskLane
{
    Scan,
    Metadata,
    Download,
    Deployment,
    StateWrite
}

public interface IBackgroundTaskCoordinator
{
    Task<T> RunAsync<T>(
        BackgroundTaskLane lane,
        string? gameKey,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);

    Task RunAsync(
        BackgroundTaskLane lane,
        string? gameKey,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}

public sealed class BackgroundTaskCoordinator : IBackgroundTaskCoordinator, IAsyncDisposable
{
    private readonly SemaphoreSlim scan = new(Math.Max(1, Environment.ProcessorCount / 2), Math.Max(1, Environment.ProcessorCount / 2));
    private readonly SemaphoreSlim metadata = new(2, 2);
    private readonly SemaphoreSlim download = new(2, 2);
    private readonly SemaphoreSlim deployment = new(1, 1);
    private readonly SemaphoreSlim stateWrite = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> gameLocks = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public Task RunAsync(
        BackgroundTaskLane lane,
        string? gameKey,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default) =>
        RunAsync(lane, gameKey, async token =>
        {
            await work(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task<T> RunAsync<T>(
        BackgroundTaskLane lane,
        string? gameKey,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        var laneLock = Lane(lane);
        await laneLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        SemaphoreSlim? gameLock = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(gameKey))
            {
                gameLock = GetGameLock(gameKey);
                await gameLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (gameLock is not null) gameLock.Release();
            laneLock.Release();
        }
    }

    private SemaphoreSlim Lane(BackgroundTaskLane lane) => lane switch
    {
        BackgroundTaskLane.Scan => scan,
        BackgroundTaskLane.Metadata => metadata,
        BackgroundTaskLane.Download => download,
        BackgroundTaskLane.Deployment => deployment,
        BackgroundTaskLane.StateWrite => stateWrite,
        _ => scan
    };

    private SemaphoreSlim GetGameLock(string gameKey)
    {
        lock (gate)
        {
            if (!gameLocks.TryGetValue(gameKey, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                gameLocks[gameKey] = semaphore;
            }

            return semaphore;
        }
    }

    public async ValueTask DisposeAsync()
    {
        scan.Dispose();
        metadata.Dispose();
        download.Dispose();
        deployment.Dispose();
        stateWrite.Dispose();
        SemaphoreSlim[] locks;
        lock (gate) locks = gameLocks.Values.ToArray();
        foreach (var item in locks) item.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
