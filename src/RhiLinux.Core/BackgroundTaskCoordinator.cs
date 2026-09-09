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
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource completion = Completed();
    private int activeOperations;
    private bool disposed;

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
        BeginOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        var laneLock = Lane(lane);
        var laneAcquired = false;
        SemaphoreSlim? gameLock = null;
        var gameAcquired = false;
        try
        {
            await laneLock.WaitAsync(token).ConfigureAwait(false);
            laneAcquired = true;
            if (!string.IsNullOrWhiteSpace(gameKey))
            {
                gameLock = GetGameLock(gameKey);
                await gameLock.WaitAsync(token).ConfigureAwait(false);
                gameAcquired = true;
            }

            return await work(token).ConfigureAwait(false);
        }
        finally
        {
            if (gameAcquired) gameLock!.Release();
            if (laneAcquired) laneLock.Release();
            EndOperation();
        }
    }

    private void BeginOperation()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeOperations++ == 0)
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void EndOperation()
    {
        lock (gate)
        {
            activeOperations--;
            if (activeOperations == 0) completion.TrySetResult();
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
        Task pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending = completion.Task;
        }
        lifetime.Cancel();
        await pending.ConfigureAwait(false);
        scan.Dispose();
        metadata.Dispose();
        download.Dispose();
        deployment.Dispose();
        stateWrite.Dispose();
        SemaphoreSlim[] locks;
        lock (gate) locks = gameLocks.Values.ToArray();
        foreach (var item in locks) item.Dispose();
        lifetime.Dispose();
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
