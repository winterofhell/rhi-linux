using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Gui;

public sealed partial class MainViewModel
{
    public bool CanConfigureOptiScaler => CanConfigure(ComponentKind.OptiScaler);
    public bool CanConfigureReShade => CanConfigure(ComponentKind.ReShade);

    private bool CanConfigure(ComponentKind component) => CanInteract && !IsSelectionLoading &&
        ComponentCards.Any(card => card.Component == component && card.IsManaged &&
            card.Health is ComponentHealth.Installed or ComponentHealth.Outdated);

    public async Task<OptiScalerSettings> ReadOptiScalerSettingsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linked = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var settings = await OptiScalerSettingsService.ReadAsync(snapshot.Game.ToDeploymentTarget(), linked.Token);
        EnsureCurrentSelection(snapshot, linked.Token);
        return settings;
    }

    public Task<DeploymentPlan> BuildOptiScalerSettingsPlanAsync(
        OptiScalerSettings settings,
        CancellationToken cancellationToken = default) =>
        BuildConfigurationPlanAsync((game, token) => planner.BuildOptiScalerSettingsPlanAsync(game, settings, token), cancellationToken);

    public Task<DeploymentPlan> BuildReShadePresetPlanAsync(
        string? source,
        CancellationToken cancellationToken = default) =>
        BuildConfigurationPlanAsync((game, token) => planner.BuildReShadePresetPlanAsync(game, source, token), cancellationToken);

    private async Task<DeploymentPlan> BuildConfigurationPlanAsync(
        Func<DeploymentTarget, CancellationToken, Task<DeploymentPlan>> build,
        CancellationToken cancellationToken)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linked = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var busy = BeginBusy();
        try
        {
            var plan = StampPlan(await build(snapshot.Game.ToDeploymentTarget(), linked.Token), snapshot);
            EnsureCurrentSelection(snapshot, linked.Token);
            return plan;
        }
        finally
        {
            EndBusy(busy);
        }
    }
}
