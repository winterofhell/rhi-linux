using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed record StackStatusReport(
    GameProfileMatch Profile,
    ProxySelectionResult Proxy,
    GameArtifactResolution ArtifactResolution,
    IReadOnlyList<ComponentStatus> Components,
    OptiScalerEligibility OptiScalerEligibility,
    bool CanInstallRecommendedStack,
    string Summary,
    StackSnapshot? Snapshot = null);

public sealed class StackStatusService(HttpClient httpClient, XdgPaths paths)
{
    private readonly GameProfileCatalog catalog = new(paths);

    public Task<StackStatusReport> GetAsync(
        SteamGame game,
        bool allowNetwork,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false) =>
        GetAsync(game.ToDeploymentTarget(), allowNetwork, cancellationToken, forceRefresh);

    public Task<StackStatusReport> GetAsync(
        InstalledGame game,
        bool allowNetwork,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false) =>
        GetAsync(game.ToDeploymentTarget(), allowNetwork, cancellationToken, forceRefresh);

    public async Task<StackStatusReport> GetAsync(
        DeploymentTarget game,
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
        var detectedTask = detector.DetectStackAsync(game, cancellationToken: cancellationToken);
        await Task.WhenAll(artifactsTask, detectedTask);
        var artifacts = await artifactsTask;
        profile = artifacts.Profile;
        var snapshot = await detectedTask;
        var adjustedReports = snapshot.Components
            .Select(report =>
            {
                var artifact = artifacts.Components.SingleOrDefault(x => x.Component == report.Component);
                return UpdateEvaluator.Apply(report, artifact, artifacts, profile);
            })
            .ToArray();
        var adjusted = adjustedReports.Select(StackDetector.ToComponentStatus).Select(status =>
        {
            if (status.Component != ComponentKind.RenoDx) return status;
            if (status.Health is ComponentHealth.DownloadRequired or ComponentHealth.Cached) return status;
            var artifact = artifacts.Components.SingleOrDefault(x => x.Component == ComponentKind.RenoDx);
            if (artifact is not null &&
                status.Lifecycle == ComponentLifecycleState.NotInstalled &&
                status.Health is ComponentHealth.Available or ComponentHealth.Unsupported &&
                !IsManualOnlyRenoExplanation(status.Explanation))
            {
                return status with
                {
                    Health = artifact.CacheState == ArtifactCacheState.Cached
                        ? ComponentHealth.Cached
                        : ComponentHealth.DownloadRequired,
                    Version = artifact.Version
                };
            }
            return status;
        }).ToArray();
        var ownershipUnavailable = adjusted.Any(x => x.Health == ComponentHealth.ManifestUnavailable);
        bool IsUsable(ComponentKind component) => adjusted.Single(x => x.Component == component).Health is
            not ComponentHealth.Conflicting and not ComponentHealth.ForeignInstallation and
            not ComponentHealth.ManifestUnavailable;
        var canInstallRenoSetup = (artifacts.CanAcquireRenoSetup || artifacts.CanAcquireRenoDx) &&
            IsUsable(ComponentKind.ReShade) && IsUsable(ComponentKind.RenoDx) &&
            artifacts.Artifacts.Any(x => x.Component == ComponentKind.RenoDx);
        var canInstallOptiScaler = eligibility.CanInstall && artifacts.CanAcquireOptiScaler &&
            IsUsable(ComponentKind.OptiScaler);
        var canInstall = proxy.HasSafeProxy && !ownershipUnavailable &&
            (canInstallRenoSetup || canInstallOptiScaler);
        var summary = !proxy.HasSafeProxy ? proxy.Reason : !artifacts.IsFullyAutomatic && !artifacts.CanAcquireRenoDx
            ? "One or more required official files could not be found."
            : ownershipUnavailable ? "Ownership metadata could not be verified, so changes are blocked."
            : !canInstallRenoSetup && !canInstallOptiScaler
                ? "No safe automatic setup is available right now."
            : adjusted.Any(x => x.Lifecycle == ComponentLifecycleState.RepairRequired ||
                x.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable) ? "Repair the managed installation."
            : adjusted.Any(x => x.Health == ComponentHealth.Outdated ||
                x.Lifecycle == ComponentLifecycleState.UpdateAvailable) ? "An update is available for installed components."
            : adjusted.Any(x => x.Health == ComponentHealth.Installed ||
                x.Lifecycle is ComponentLifecycleState.InstalledHealthy or
                    ComponentLifecycleState.InstalledWithWarnings or
                    ComponentLifecycleState.InstalledMetadataIncomplete) ? "The managed setup is installed and consistent."
            : "The recommended setup can be installed.";
        var defects = adjustedReports
            .Where(x => x.State == ComponentLifecycleState.RepairRequired)
            .Select(x => x.Evidence.RepairReason ?? x.Explanation)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var finalSnapshot = snapshot with
        {
            Components = adjustedReports,
            Summary = summary,
            Generation = forceRefresh ? snapshot.Generation + 1 : snapshot.Generation,
            ConcreteDefects = defects,
            ReShadeArtifactFingerprint = UpdateEvaluator.BuildFingerprint(
                adjustedReports.Single(x => x.Component == ComponentKind.ReShade)),
            RenoDxArtifactFingerprint = UpdateEvaluator.BuildFingerprint(
                adjustedReports.Single(x => x.Component == ComponentKind.RenoDx)),
            OptiScalerArtifactFingerprint = UpdateEvaluator.BuildFingerprint(
                adjustedReports.Single(x => x.Component == ComponentKind.OptiScaler))
        };
        return new(profile, proxy, artifacts, adjusted, eligibility, canInstall, summary, finalSnapshot);
    }

    private static bool IsManualOnlyRenoExplanation(string explanation) =>
        explanation.Contains("manual download", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("No direct addon download", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("No RenoDX addon found", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("catalog unavailable", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("require confirmation", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("another executable", StringComparison.OrdinalIgnoreCase);
}
