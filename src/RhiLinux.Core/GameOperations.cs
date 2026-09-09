namespace RhiLinux.Core;

public sealed record OperationPreview(
    DeploymentPlan Plan,
    DeploymentPlanSummary Summary,
    ExecutionResult Validation);

public sealed record GameOperationProgress(
    GameInstallId InstallId,
    string Stage,
    int CompletedOperations,
    int TotalOperations);

public interface IGameOperationCoordinator : IAsyncDisposable
{
    event EventHandler<GameOperationProgress>? Progress;
    bool IsBusy => false;

    Task<OperationPreview> PreviewAsync(
        DeploymentPlan plan,
        CancellationToken cancellationToken = default);

    Task<ExecutionResult> ExecuteApprovedAsync(
        DeploymentPlan plan,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<ExecutionResult> RecoverAsync(
        DeploymentPlan recoveryPlan,
        bool approved,
        CancellationToken cancellationToken = default);
}
