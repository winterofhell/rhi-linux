using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed record StackStatusReport(
    GameProfileMatch Profile,
    ProxySelectionResult Proxy,
    GameArtifactResolution ArtifactResolution,
    IReadOnlyList<ComponentStatus> Components,
    OptiScalerEligibility OptiScalerEligibility,
    bool CanInstallRecommendedStack,
    string Summary);

public sealed class StackStatusService(HttpClient httpClient, XdgPaths paths)
{
    private readonly GameProfileCatalog catalog = new(paths);

    public async Task<StackStatusReport> GetAsync(
        SteamGame game,
        bool allowNetwork,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var proxyService = new ProxyDiagnosticsService(catalog);
        var profile = await catalog.MatchAsync(game, cancellationToken);
        var proxy = await proxyService.DiagnoseAsync(game, cancellationToken);
        var eligibility = OptiScalerEligibilityService.Evaluate(game, profile, proxy);
        var artifacts = await new OfficialArtifactResolver(httpClient, paths, catalog).ResolveAsync(
            game, allowNetwork, cancellationToken, forceRefresh);
        var detected = await new ComponentDetector(catalog, proxyService).DetectAsync(game, cancellationToken);
        var adjusted = detected.Select(status => ApplyArtifactState(status, artifacts)).ToArray();
        var ownershipUnavailable = adjusted.Any(x => x.Health == ComponentHealth.ManifestUnavailable);
        bool IsUsable(ComponentKind component) => adjusted.Single(x => x.Component == component).Health is
            not ComponentHealth.Conflicting and not ComponentHealth.ForeignInstallation and
            not ComponentHealth.ManifestUnavailable;
        var canInstallRenoSetup = artifacts.CanAcquireRenoSetup &&
            IsUsable(ComponentKind.ReShade) && IsUsable(ComponentKind.RenoDx);
        var canInstallOptiScaler = eligibility.CanInstall && artifacts.CanAcquireOptiScaler &&
            IsUsable(ComponentKind.OptiScaler);
        var canInstall = proxy.HasSafeProxy && !ownershipUnavailable &&
            (canInstallRenoSetup || canInstallOptiScaler);
        var summary = !proxy.HasSafeProxy ? proxy.Reason : !artifacts.IsFullyAutomatic
            ? "One or more required official artifacts could not be resolved."
            : ownershipUnavailable ? "The ownership manifest could not be verified; modifying actions are blocked."
            : !canInstallRenoSetup && !canInstallOptiScaler
                ? "No independently safe automatic component setup is currently available."
            : adjusted.Any(x => x.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable) ? "Repair the managed installation."
            : adjusted.Any(x => x.Health == ComponentHealth.Outdated) ? "A dependency-aware stack update is available."
            : adjusted.Any(x => x.Health == ComponentHealth.Installed) ? "The managed stack is installed and consistent."
            : "The recommended stack can be installed automatically.";
        return new(profile, proxy, artifacts, adjusted, eligibility, canInstall, summary);
    }

    private static ComponentStatus ApplyArtifactState(ComponentStatus status, GameArtifactResolution resolution)
    {
        var artifact = resolution.Artifacts.SingleOrDefault(x => x.Component == status.Component);
        if (artifact is null) return status;
        if (status.Health == ComponentHealth.Installed && artifact.CacheState == ArtifactCacheState.DownloadRequired &&
            artifact.Validation == ArtifactValidationState.Valid)
            return status with { Health = ComponentHealth.Outdated, Explanation = "A newer official build is available." };
        if (status.Health == ComponentHealth.Installed && status.Version is not null &&
            artifact.Version is not "snapshot" and not "rolling" && !status.Version.Equals(artifact.Version, StringComparison.OrdinalIgnoreCase))
            return status with { Health = ComponentHealth.Outdated, Explanation = $"Installed {status.Version}; official release {artifact.Version} is available ({artifact.CacheState})." };
        if (status.Health is ComponentHealth.Available or ComponentHealth.DownloadRequired or ComponentHealth.Supported or ComponentHealth.Experimental)
            return status with
            {
                Health = status.Health == ComponentHealth.Experimental ? ComponentHealth.Experimental :
                    artifact.CacheState == ArtifactCacheState.Cached ? ComponentHealth.Cached : ComponentHealth.DownloadRequired,
                Version = artifact.Version,
                Explanation = artifact.CacheState == ArtifactCacheState.Cached
                    ? $"Official version {artifact.Version} is ready for offline use."
                    : $"Official version {artifact.Version} will be downloaded automatically."
            };
        return status;
    }
}
