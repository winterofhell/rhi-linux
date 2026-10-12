using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Gui;

public sealed partial class MainViewModel
{
    private readonly EnhancementPlanner? enhancementPlanner;
    private readonly EnhancementExecutor enhancementExecutor = new();
    private EnhancementSnapshot? enhancementSnapshot;
    private string? enhancementInstallId;
    private IReadOnlyList<EnhancementUpdate> enhancementUpdates = [];
    private IReadOnlyList<EnhancementCardViewModel> enhancementCards = [];
    private long enhancementGeneration;
    private bool isEnhancementBusy;

    public bool EnhancementsAvailable => enhancementPlanner is not null;
    public IReadOnlyList<EnhancementCardViewModel> EnhancementCards
    {
        get => enhancementCards;
        private set
        {
            if (!Set(ref enhancementCards, value)) return;
            OnPropertyChanged(nameof(ShowEnhancements));
        }
    }
    public bool ShowEnhancements => EnhancementsAvailable && EnhancementCards.Count > 0;
    public bool IsEnhancementBusy { get => isEnhancementBusy; private set => Set(ref isEnhancementBusy, value); }
    public bool EnhancementRecoveryPending => CurrentEnhancementSnapshot?.RecoveryPending == true;
    public IReadOnlyCollection<string> InstalledShaderPackIds =>
        CurrentEnhancementSnapshot?.ShaderPacks.Select(pack => pack.Id).ToArray() ?? [];
    public int EnhancementUpdateCount => CurrentEnhancementSnapshot is null ? 0 : enhancementUpdates.Count;

    private EnhancementSnapshot? CurrentEnhancementSnapshot =>
        enhancementSnapshot is not null && enhancementInstallId == SelectedGame?.EffectiveInstallId ? enhancementSnapshot : null;

    private IReadOnlyCollection<string> EnhancementDllOverrides =>
        CurrentEnhancementSnapshot?.AdditionalDllOverrides ?? [];

    public async Task RefreshEnhancementsAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is { } game) await LoadEnhancementsAsync(game, cancellationToken);
    }

    public Task<EnhancementPlan> BuildEnhancementInstallPlanAsync(
        EnhancementKind kind,
        CancellationToken cancellationToken = default) =>
        WithEnhancementTarget(target => RequirePlanner().BuildComponentInstallAsync(
            target, kind, cancellationToken, enhancementUpdates.Any(update => update.Kind == kind)));

    public Task<EnhancementPlan> BuildEnhancementRemovePlanAsync(
        EnhancementKind kind,
        CancellationToken cancellationToken = default) =>
        WithEnhancementTarget(target => RequirePlanner().BuildComponentRemoveAsync(target, kind, cancellationToken));

    public Task<EnhancementPlan> BuildShaderPackPlanAsync(
        IReadOnlyCollection<string> packIds,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default) =>
        WithEnhancementTarget(target => RequirePlanner().BuildShaderPlanAsync(
            target, packIds, progress, cancellationToken,
            enhancementUpdates.Any(update => update.Kind == EnhancementKind.ShaderPacks)));

    public Task<EnhancementPlan> BuildRemoveAllEnhancementsPlanAsync(CancellationToken cancellationToken = default) =>
        WithEnhancementTarget(target => RequirePlanner().BuildRemoveEverythingAsync(target, cancellationToken));

    public bool IsEnhancementPlanForCurrentSelection(EnhancementPlan plan) =>
        SelectedGame is { } game && SameTarget(game.ToDeploymentTarget(), plan.Game);

    public void DiscardEnhancementPlan(EnhancementPlan plan) => EnhancementPlanner.Discard(plan);

    public async Task<EnhancementExecutionResult> ExecuteEnhancementPlanAsync(
        EnhancementPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnhancementPlanForCurrentSelection(plan))
        {
            EnhancementPlanner.Discard(plan);
            return new(false, false, [], "The selected game changed before the operation began. No files were changed.");
        }
        var game = SelectedGame!;
        var busy = BeginBusy();
        IsEnhancementBusy = true;
        EnhancementExecutionResult result;
        try
        {
            result = await enhancementExecutor.ExecuteAsync(plan, false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = new(false, false, [], "Operation cancelled. Completed steps were rolled back.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            result = new(false, false, [], exception.Message);
        }
        finally
        {
            IsEnhancementBusy = false;
            EndBusy(busy);
        }
        await applicationLog.WriteAsync(new(DateTimeOffset.UtcNow,
            result.Succeeded ? ApplicationLogLevel.Information : ApplicationLogLevel.Error,
            "enhancement-operation",
            $"{plan.Title}: {(result.Succeeded ? "completed" : "failed")}",
            result.Error), CancellationToken.None);
        try
        {
            await activityHistoryStore.AppendAsync(new(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                game.InstallId,
                game.Name,
                plan.Title,
                result.Succeeded ? ActivityOutcome.Success : ActivityOutcome.RolledBack,
                plan.Steps.Count(step => step.Type is not (EnhancementStepType.SetIniValue or EnhancementStepType.RestoreIniValue)),
                plan.Steps.Count(step => step.Type == EnhancementStepType.PreserveOriginalShaders),
                plan.ResultingState.Files.Any(file => EnhancementCatalog.Get(file.Kind).DllOverride is not null)
                    ? EnhancementPlanner.ComposeLaunchOption(null, plan.ResultingState.Files
                        .Select(file => EnhancementCatalog.Get(file.Kind).DllOverride)
                        .OfType<string>()
                        .ToArray())
                    : null,
                plan.Steps.Select(step => step.Target).Distinct(StringComparer.Ordinal).ToArray(),
                result.Error), CancellationToken.None);
            await ReloadActivityHistoryAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            GlobalStatus = $"{GlobalStatus} · activity history could not be saved";
        }
        if (SelectedGame?.EffectiveInstallId == game.EffectiveInstallId)
        {
            enhancementUpdates = [];
            await SelectAsync(SelectedGame);
        }
        return result;
    }

    public async Task<bool> RecoverEnhancementsAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is not { } game) return false;
        var recovered = await enhancementExecutor.RecoverAsync(game.GameRoot, cancellationToken);
        await LoadEnhancementsAsync(game, cancellationToken);
        return recovered;
    }

    private async Task LoadEnhancementsAsync(InstalledGame game, CancellationToken cancellationToken)
    {
        if (enhancementPlanner is null) return;
        var generation = Interlocked.Increment(ref enhancementGeneration);
        if (enhancementInstallId != game.EffectiveInstallId)
        {
            enhancementSnapshot = null;
            enhancementUpdates = [];
            enhancementInstallId = game.EffectiveInstallId;
            EnhancementCards = [];
        }
        if (!game.IsActionable || game.IsNativeLinux || string.IsNullOrWhiteSpace(game.DeploymentDirectory))
        {
            EnhancementCards = [];
            RaiseEnhancementProperties();
            return;
        }
        EnhancementSnapshot snapshot;
        try
        {
            snapshot = await enhancementPlanner.InspectAsync(game.ToDeploymentTarget(), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (generation != Volatile.Read(ref enhancementGeneration)) return;
            EnhancementCards = [EnhancementCardViewModel.Failed(exception.Message)];
            RaiseEnhancementProperties();
            return;
        }
        if (generation != Volatile.Read(ref enhancementGeneration) || SelectedGame?.EffectiveInstallId != game.EffectiveInstallId) return;
        enhancementSnapshot = snapshot;
        enhancementInstallId = game.EffectiveInstallId;
        EnhancementCards = EnhancementCardViewModel.Create(snapshot, enhancementUpdates);
        RaiseEnhancementProperties();
    }

    private async Task CheckEnhancementUpdatesAsync(InstalledGame game, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (enhancementPlanner is null || !game.IsActionable || game.IsNativeLinux) return;
        IReadOnlyList<EnhancementUpdate> updates;
        try
        {
            updates = await enhancementPlanner.CheckUpdatesAsync(game.ToDeploymentTarget(), cancellationToken, forceRefresh);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException)
        {
            return;
        }
        if (SelectedGame?.EffectiveInstallId != game.EffectiveInstallId) return;
        enhancementUpdates = updates;
        if (CurrentEnhancementSnapshot is { } snapshot)
            EnhancementCards = EnhancementCardViewModel.Create(snapshot, updates);
        RaiseEnhancementProperties();
    }

    private void ClearEnhancements()
    {
        Interlocked.Increment(ref enhancementGeneration);
        enhancementSnapshot = null;
        enhancementInstallId = null;
        enhancementUpdates = [];
        EnhancementCards = [];
        RaiseEnhancementProperties();
    }

    private void RaiseEnhancementProperties()
    {
        foreach (var property in new[]
                 {
                     nameof(ShowEnhancements), nameof(EnhancementRecoveryPending), nameof(InstalledShaderPackIds),
                     nameof(EnhancementUpdateCount), nameof(LaunchOption), nameof(HasLaunchOption), nameof(LaunchOptionDescription)
                 })
            OnPropertyChanged(property);
    }

    private async Task<EnhancementPlan> WithEnhancementTarget(Func<DeploymentTarget, Task<EnhancementPlan>> build)
    {
        if (SelectedGame is not { } game) throw new InvalidOperationException("Select a game first.");
        if (!game.IsActionable || game.IsNativeLinux)
            throw new InvalidOperationException("Enhancements are only available for Windows games running through Proton.");
        return await build(game.ToDeploymentTarget());
    }

    private EnhancementPlanner RequirePlanner() =>
        enhancementPlanner ?? throw new InvalidOperationException("Enhancements are not available in this session.");

    private static bool SameTarget(DeploymentTarget left, DeploymentTarget right) =>
        string.Equals(left.GameRoot, right.GameRoot, StringComparison.Ordinal) &&
        string.Equals(left.DeploymentDirectory, right.DeploymentDirectory, StringComparison.Ordinal) &&
        string.Equals(left.Executable, right.Executable, StringComparison.Ordinal);
}
