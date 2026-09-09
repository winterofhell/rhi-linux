using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed class DelegateRecommendedDeploymentPlanBuilder(
    Func<InstalledGame, RecommendedSetupResult, CancellationToken, Task<DeploymentPlan>> build)
    : IRecommendedDeploymentPlanBuilder
{
    public Task<DeploymentPlan> BuildAsync(
        InstalledGame game,
        RecommendedSetupResult recommendation,
        CancellationToken cancellationToken = default) =>
        build(game, recommendation, cancellationToken);
}

public sealed class RecommendedDeploymentPlanBuilder(
    DeploymentPlanner planner,
    Func<InstalledGame, RecommendedSetupResult, CancellationToken, Task<RecommendedStackArtifacts>> resolveArtifacts)
    : IRecommendedDeploymentPlanBuilder
{
    public async Task<DeploymentPlan> BuildAsync(
        InstalledGame game,
        RecommendedSetupResult recommendation,
        CancellationToken cancellationToken = default)
    {
        if (recommendation.PrimaryOptionId is null ||
            recommendation.Options.FirstOrDefault(option => option.Id == recommendation.PrimaryOptionId) is not
            { IsSupported: true } selected)
            throw new InvalidOperationException("The recommended setup has no supported primary option.");

        if (selected.Id is "keep-reshade" or "keep-current" or "up-to-date" or "inspect")
            return NoChangePlan(game, selected.Title);
        if (selected.Id == "restore-backups")
            return await planner.BuildRestorePlanAsync(game, cancellationToken).ConfigureAwait(false);

        var artifacts = await resolveArtifacts(game, recommendation, cancellationToken).ConfigureAwait(false);
        return selected.Id switch
        {
            "install-reshade" when artifacts.ReShade is not null =>
                await planner.BuildInstallPlanAsync(game, artifacts.ReShade, cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
            "update-reshade" when artifacts.ReShade is not null =>
                await planner.BuildUpdatePlanAsync(game, artifacts.ReShade, cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
            _ => await planner.BuildRecommendedStackPlanAsync(game, artifacts, cancellationToken: cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private static DeploymentPlan NoChangePlan(InstalledGame game, string action) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        InstallId = game.EffectiveInstallId,
        SteamAppId = game.SteamAppId,
        GameRoot = game.GameRoot,
        DeploymentDirectory = game.DeploymentDirectory ?? game.GameRoot,
        Action = action,
        RequiresConfirmation = false,
        CompatibilityMessage = "No filesystem changes are required."
    };
}

public sealed class OfficialRecommendedDeploymentPlanBuilder(
    HttpClient httpClient,
    XdgPaths paths,
    DeploymentPlanner planner,
    bool allowNetwork = false) : IRecommendedDeploymentPlanBuilder
{
    private readonly GameProfileCatalog catalog = new(paths);
    private readonly ArtifactCacheService cache = new(paths);

    public async Task<DeploymentPlan> BuildAsync(
        InstalledGame game,
        RecommendedSetupResult recommendation,
        CancellationToken cancellationToken = default)
    {
        if (recommendation.PrimaryOptionId is "keep-reshade" or "keep-current" or "up-to-date" or "inspect")
            return new DeploymentPlan
            {
                Id = Guid.NewGuid().ToString("N"),
                InstallId = game.EffectiveInstallId,
                SteamAppId = game.SteamAppId,
                GameRoot = game.GameRoot,
                DeploymentDirectory = game.DeploymentDirectory ?? game.GameRoot,
                Action = recommendation.Options.First(option => option.Id == recommendation.PrimaryOptionId).Title,
                RequiresConfirmation = false,
                CompatibilityMessage = "No filesystem changes are required."
            };
        if (recommendation.PrimaryOptionId == "restore-backups")
            return await planner.BuildRestorePlanAsync(game, cancellationToken).ConfigureAwait(false);

        var preflight = ManagedArchiveExtractor.ProbeCapabilities(
            paths.AppCacheDirectory,
            Path.Combine(paths.AppCacheDirectory, "tmp"));
        if (!preflight.RequiredCapabilitiesAvailable)
            throw new InvalidOperationException(preflight.Summary);

        var resolver = new OfficialArtifactResolver(httpClient, paths, catalog);
        var resolution = await resolver.ResolveAsync(
            game,
            allowNetwork,
            cancellationToken,
            forceRefresh: false).ConfigureAwait(false);
        var requested = SelectComponents(recommendation, resolution);
        if (requested.Count == 0)
            throw new InvalidOperationException("No official artifact is available for the selected recommendation.");
        resolution = await resolver.AcquireSelectedAsync(
            resolution,
            requested,
            allowNetwork,
            cancellationToken).ConfigureAwait(false);

        var target = ApplyDeploymentHint(game, resolution);
        ComponentArtifact? Materialize(ComponentKind component)
        {
            var selection = resolution.Artifacts.SingleOrDefault(item =>
                item.Component == component && item.CacheState == ArtifactCacheState.Cached && item.CachedPath is not null);
            return selection is null ? null : CachedArtifactMaterializer.MaterializeSingle(selection);
        }

        var reshade = requested.Contains(ComponentKind.ReShade) ? Materialize(ComponentKind.ReShade) : null;
        var renoDx = requested.Contains(ComponentKind.RenoDx) ? Materialize(ComponentKind.RenoDx) : null;
        (ComponentArtifact Main, IReadOnlyList<ComponentArtifact> Support, OptiScalerBundleManifest Manifest)? opti = null;
        if (requested.Contains(ComponentKind.OptiScaler) &&
            resolution.Artifacts.SingleOrDefault(item => item.Component == ComponentKind.OptiScaler) is { } optiSelection &&
            optiSelection.CacheState == ArtifactCacheState.Cached)
            opti = await CachedArtifactMaterializer.MaterializeOptiScalerAsync(cache, optiSelection, cancellationToken)
                .ConfigureAwait(false);

        if (requested.Contains(ComponentKind.ReShade) && reshade is null ||
            requested.Contains(ComponentKind.RenoDx) && renoDx is null ||
            requested.Contains(ComponentKind.OptiScaler) && opti is null)
            throw new InvalidOperationException(
                allowNetwork
                    ? "One or more official artifacts could not be acquired."
                    : "The official artifacts required for this plan are not cached. Open Updates to fetch them first.");

        var proxy = await new ProxyDiagnosticsService(catalog).DiagnoseAsync(target, cancellationToken)
            .ConfigureAwait(false);
        return await planner.BuildRecommendedStackPlanAsync(
            target,
            new(reshade, renoDx, opti?.Main, opti?.Support, opti?.Manifest),
            proxy,
            cancellationToken).ConfigureAwait(false);
    }

    private static HashSet<ComponentKind> SelectComponents(
        RecommendedSetupResult recommendation,
        GameArtifactResolution resolution)
    {
        var selected = recommendation.PrimaryOptionId;
        var result = new HashSet<ComponentKind>();
        if (selected is "install-reshade" or "update-reshade")
        {
            result.Add(ComponentKind.ReShade);
            return result;
        }
        if (selected is "add-renodx")
        {
            result.Add(ComponentKind.ReShade);
            result.Add(ComponentKind.RenoDx);
            return result;
        }
        if (selected is "add-optiscaler" or "add-optiscaler-coexist")
        {
            result.Add(ComponentKind.OptiScaler);
            return result;
        }

        if (resolution.Artifacts.Any(item => item.Component == ComponentKind.ReShade))
            result.Add(ComponentKind.ReShade);
        if ((resolution.CanAcquireRenoSetup || resolution.CanAcquireRenoDx) &&
            resolution.Artifacts.Any(item => item.Component == ComponentKind.RenoDx))
            result.Add(ComponentKind.RenoDx);
        if (resolution.CanAcquireOptiScaler && resolution.Artifacts.Any(item => item.Component == ComponentKind.OptiScaler))
            result.Add(ComponentKind.OptiScaler);
        return result;
    }

    private static InstalledGame ApplyDeploymentHint(InstalledGame game, GameArtifactResolution resolution)
    {
        var relative = resolution.RecommendedDeploymentRelativeDirectory;
        if (string.IsNullOrWhiteSpace(relative)) return game;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.GameRoot));
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (candidate.Equals(root, StringComparison.Ordinal) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return game with { DeploymentDirectory = candidate };
        return game;
    }
}
