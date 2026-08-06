using RhiLinux.Core;
using RhiLinux.Sources;
using RhiLinux.Steam;

namespace RhiLinux.Gui;

public sealed class LibraryWatchService : IAsyncDisposable
{
    private readonly SourceWatcherCoordinator coordinator;
    private readonly Func<CancellationToken, Task> onRefresh;
    private CancellationTokenSource? refreshCts;
    private bool disposed;

    public LibraryWatchService(Func<CancellationToken, Task> onRefresh, TimeSpan? debounce = null)
    {
        this.onRefresh = onRefresh;
        coordinator = new SourceWatcherCoordinator(OnEvents, debounce);
    }

    public int ActiveWatcherCount => coordinator.ActiveWatcherCount;

    public void ReplaceTargets(IEnumerable<SourceWatchTarget> targets) =>
        coordinator.ReplaceTargets(targets);

    public void Stop() => coordinator.ReplaceTargets([]);

    public static async Task<IReadOnlyList<SourceWatchTarget>> CollectTargetsAsync(
        GameSourceDiscoveryContext context,
        IReadOnlyList<IGameSourceProvider> providers,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<SourceWatchTarget>();
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<GameSourceRoot> roots;
            try
            {
                roots = await provider.DiscoverRootsAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var root in roots.Where(item => item.Exists && item.Readable && !item.Deduplicated))
            {
                targets.AddRange(provider.Id switch
                {
                    SteamGameSourceProvider.ProviderId => SourceWatchTargets.ForSteam(root),
                    "heroic" => SourceWatchTargets.ForHeroic(root),
                    "legendary" => SourceWatchTargets.ForLegendary(root),
                    "lutris" => SourceWatchTargets.ForLutris(root),
                    "bottles" => SourceWatchTargets.ForBottles(root),
                    "minigalaxy" => SourceWatchTargets.ForMinigalaxy(root),
                    "manual" => SourceWatchTargets.ForManual(root),
                    _ => []
                });
            }
        }

        return targets;
    }

    private void OnEvents(IReadOnlyList<SourceWatchEvent> events)
    {
        if (disposed || events.Count == 0) return;
        refreshCts?.Cancel();
        refreshCts?.Dispose();
        refreshCts = new CancellationTokenSource();
        var token = refreshCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token).ConfigureAwait(false);
                await onRefresh(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, token);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        refreshCts?.Cancel();
        refreshCts?.Dispose();
        await coordinator.DisposeAsync().ConfigureAwait(false);
    }
}
