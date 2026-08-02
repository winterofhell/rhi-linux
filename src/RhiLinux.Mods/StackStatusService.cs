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
        var detector = new ComponentDetector(catalog, proxyService);
        var resolver = new OfficialArtifactResolver(httpClient, paths, catalog);

        var profileTask = catalog.MatchAsync(game, cancellationToken);
        var proxyTask = proxyService.DiagnoseAsync(game, cancellationToken);
        await Task.WhenAll(profileTask, proxyTask);
        var profile = await profileTask;
        var proxy = await proxyTask;
        var eligibility = OptiScalerEligibilityService.Evaluate(game, profile, proxy);

        var artifactsTask = resolver.ResolveAsync(game, allowNetwork, cancellationToken, forceRefresh);
        var detectedTask = detector.DetectAsync(game, cancellationToken);
        await Task.WhenAll(artifactsTask, detectedTask);
        var artifacts = await artifactsTask;
        profile = artifacts.Profile;
        var adjusted = (await detectedTask).Select(status => ApplyArtifactState(status, artifacts)).ToArray();
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
            ? "One or more required official files could not be found."
            : ownershipUnavailable ? "Ownership metadata could not be verified, so changes are blocked."
            : !canInstallRenoSetup && !canInstallOptiScaler
                ? "No safe automatic setup is available right now."
            : adjusted.Any(x => x.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable) ? "Repair the managed installation."
            : adjusted.Any(x => x.Health == ComponentHealth.Outdated) ? "An update is available for installed components."
            : adjusted.Any(x => x.Health == ComponentHealth.Installed) ? "The managed setup is installed and consistent."
            : "The recommended setup can be installed.";
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
