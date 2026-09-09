using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed class GameOperationCoordinator : IGameOperationCoordinator
{
    private readonly IDeploymentExecutor executor;
    private readonly IGameReadinessService readiness;
    private readonly Func<GameInstallId, CancellationToken, Task>? refreshLibrary;
    private readonly BackgroundTaskCoordinator tasks;
    private readonly IOperationPreflightService? preflight;
    private bool disposed;
    private int activeOperations;

    public GameOperationCoordinator(
        IDeploymentExecutor executor,
        IGameReadinessService readiness,
        Func<GameInstallId, CancellationToken, Task>? refreshLibrary = null,
        BackgroundTaskCoordinator? tasks = null,
        IOperationPreflightService? preflight = null)
    {
        this.executor = executor;
        this.readiness = readiness;
        this.refreshLibrary = refreshLibrary;
        this.tasks = tasks ?? new BackgroundTaskCoordinator();
        this.preflight = preflight;
    }

    public event EventHandler<GameOperationProgress>? Progress;
    public bool IsBusy => Volatile.Read(ref activeOperations) > 0;

    public async Task<OperationPreview> PreviewAsync(
        DeploymentPlan plan,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Interlocked.Increment(ref activeOperations);
        try
        {
            var installId = ParseInstallId(plan);
            var validation = await tasks.RunAsync(
                BackgroundTaskLane.Deployment,
                installId.Value,
                token => executor.ExecuteAsync(plan, true, token),
                cancellationToken).ConfigureAwait(false);
            return new(plan, DeploymentPlanSummary.From(plan), validation);
        }
        finally
        {
            Interlocked.Decrement(ref activeOperations);
        }
    }

    public Task<ExecutionResult> ExecuteApprovedAsync(
        DeploymentPlan plan,
        bool approved,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(plan, approved, "Executing approved deployment", cancellationToken);

    public async Task<ExecutionResult> RecoverAsync(
        DeploymentPlan recoveryPlan,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(recoveryPlan, approved, "Executing approved recovery", cancellationToken)
            .ConfigureAwait(false);
        if (result.RolledBack && result.Error?.Contains("interrupted transaction", StringComparison.OrdinalIgnoreCase) == true)
            return result with
            {
                Succeeded = true,
                Error = null,
                Warning = "The interrupted operation was restored to its previous state. Build a fresh plan before making new changes.",
                State = OperationLifecycleState.SucceededWithWarning
            };
        return result;
    }

    private async Task<ExecutionResult> ExecuteAsync(
        DeploymentPlan plan,
        bool approved,
        string stage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!approved)
            return new(false, false, false, [], "Explicit approval is required before game files can be changed.");
        Interlocked.Increment(ref activeOperations);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installId = ParseInstallId(plan);
            if (preflight is not null)
            {
                var check = await preflight.CheckAsync(plan, cancellationToken).ConfigureAwait(false);
                if (!check.CanProceed)
                {
                    var errors = check.Issues
                        .Where(issue => issue.Severity == OperationPreflightSeverity.Blocking)
                        .Select(issue => $"{issue.Title}: {issue.Message}")
                        .ToArray();
                    return new(false, false, false, errors,
                        errors.FirstOrDefault() ?? "The operation did not pass safety checks.",
                        OperationLifecycleState.FailedBeforeChanges);
                }
            }
            Progress?.Invoke(this, new(installId, stage, 0, plan.Operations.Count));
            var result = await tasks.RunAsync(
                BackgroundTaskLane.Deployment,
                installId.Value,
                token => executor.ExecuteAsync(plan, false, token),
                cancellationToken).ConfigureAwait(false);
            readiness.Invalidate(installId);
            if (refreshLibrary is not null)
                await refreshLibrary(installId, cancellationToken).ConfigureAwait(false);
            Progress?.Invoke(this, new(installId, result.Succeeded ? "Completed" : "Failed",
                result.Succeeded ? plan.Operations.Count : 0, plan.Operations.Count));
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref activeOperations);
        }
    }

    private static GameInstallId ParseInstallId(DeploymentPlan plan) =>
        GameInstallId.TryParse(plan.InstallId, out var installId) && !installId.IsEmpty
            ? installId
            : throw new InvalidDataException("The deployment plan does not contain a valid game installation identity.");

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await tasks.DisposeAsync().ConfigureAwait(false);
        Progress = null;
    }
}
