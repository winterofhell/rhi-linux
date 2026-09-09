using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class GameOperationCoordinatorTests
{
    [Fact]
    public async Task PreviewIsDryRunAndApprovalIsRequiredForWrites()
    {
        var executor = new RecordingExecutor();
        var readiness = new RecordingReadiness();
        await using var coordinator = new GameOperationCoordinator(executor, readiness);
        var plan = Plan("fixture-game");

        var preview = await coordinator.PreviewAsync(plan);
        var denied = await coordinator.ExecuteApprovedAsync(plan, false);

        Assert.True(preview.Validation.DryRun);
        Assert.False(denied.Succeeded);
        Assert.Contains("approval", denied.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([true], executor.DryRunValues);
        Assert.Empty(readiness.Invalidated);
    }

    [Fact]
    public async Task GlobalDeploymentLaneSerializesWritesAndRefreshesAfterCompletion()
    {
        var executor = new RecordingExecutor(TimeSpan.FromMilliseconds(50));
        var readiness = new RecordingReadiness();
        var refreshed = new List<GameInstallId>();
        await using var coordinator = new GameOperationCoordinator(
            executor,
            readiness,
            (installId, _) =>
            {
                refreshed.Add(installId);
                return Task.CompletedTask;
            });

        await Task.WhenAll(
            coordinator.ExecuteApprovedAsync(Plan("fixture-one"), true),
            coordinator.ExecuteApprovedAsync(Plan("fixture-two"), true));

        Assert.Equal(1, executor.MaximumConcurrentCalls);
        Assert.Equal(2, readiness.Invalidated.Count);
        Assert.Equal(2, refreshed.Count);
        Assert.All(executor.DryRunValues, value => Assert.False(value));
    }

    [Fact]
    public async Task DisposalCancelsAndObservesOwnedOperation()
    {
        var executor = new RecordingExecutor(TimeSpan.FromSeconds(30));
        var coordinator = new GameOperationCoordinator(executor, new RecordingReadiness());
        var operation = coordinator.ExecuteApprovedAsync(Plan("fixture-game"), true);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static DeploymentPlan Plan(string installId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        InstallId = installId,
        GameRoot = "/fixture/game",
        DeploymentDirectory = "/fixture/game",
        Action = "fixture",
        Operations = [new(DeploymentOperationType.CreateDirectory, "/fixture/game")]
    };

    private sealed class RecordingExecutor(TimeSpan? delay = null) : IDeploymentExecutor
    {
        private int activeCalls;
        public List<bool> DryRunValues { get; } = [];
        public int MaximumConcurrentCalls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExecutionResult> ExecuteAsync(
            DeploymentPlan plan,
            bool dryRun,
            CancellationToken cancellationToken = default)
        {
            DryRunValues.Add(dryRun);
            var active = Interlocked.Increment(ref activeCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            Entered.TrySetResult();
            try
            {
                if (delay is { } duration) await Task.Delay(duration, cancellationToken);
                return new(true, dryRun, false, []);
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }
    }

    private sealed class RecordingReadiness : IGameReadinessService
    {
        public List<GameInstallId> Invalidated { get; } = [];

        public Task<GameReadinessResult> EvaluateAsync(
            InstalledGame game,
            GameReadinessEvaluationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Invalidate(GameInstallId installId) => Invalidated.Add(installId);

        public void InvalidateAll() => Invalidated.Clear();
    }
}
