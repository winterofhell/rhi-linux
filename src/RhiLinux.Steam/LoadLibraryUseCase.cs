using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed record LoadLibraryRequest(
    IReadOnlyDictionary<uint, GameOverride>? Overrides = null,
    IEnumerable<string>? SteamRoots = null,
    bool IncludeDefaultRoots = false,
    bool ForceFullAnalysis = false);

public sealed record LoadLibraryResult(
    ScanResult Scan,
    bool ServedFromCache,
    IncrementalScanMetrics? Metrics);

public interface ILoadLibraryUseCase
{
    Task<OperationResult<LoadLibraryResult>> ExecuteAsync(
        LoadLibraryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class LoadLibraryUseCase(
    SteamDiscoveryService discovery,
    ILibraryIndexStore? indexStore = null,
    IBackgroundTaskCoordinator? coordinator = null) : ILoadLibraryUseCase
{
    public async Task<OperationResult<LoadLibraryResult>> ExecuteAsync(
        LoadLibraryRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            async Task<LoadLibraryResult> Work(CancellationToken token)
            {
                var scan = await discovery.ScanAsync(
                    request.SteamRoots,
                    request.Overrides,
                    token,
                    request.IncludeDefaultRoots,
                    new ScanOptions(
                        UseLibraryIndex: indexStore is not null,
                        ForceFullAnalysis: request.ForceFullAnalysis,
                        LibraryIndexStore: indexStore)).ConfigureAwait(false);
                return new LoadLibraryResult(scan, false, discovery.LastMetrics);
            }

            var result = coordinator is null
                ? await Work(cancellationToken).ConfigureAwait(false)
                : await coordinator.RunAsync(BackgroundTaskLane.Scan, null, Work, cancellationToken).ConfigureAwait(false);
            var warnings = result.Scan.Warnings
                .Select(warning => new OperationWarning("scan-warning", warning))
                .ToArray();
            return OperationResult<LoadLibraryResult>.Success(result, warnings);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<LoadLibraryResult>.Failure(new OperationError(
                "scan-cancelled", "Library scan was cancelled.", Retryable: true, SuggestedAction: "Run the scan again."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult<LoadLibraryResult>.Failure(new OperationError(
                "scan-failed",
                "Steam library scan failed.",
                exception.Message,
                Retryable: true,
                SuggestedAction: "Check Steam library permissions and try again."));
        }
    }
}
