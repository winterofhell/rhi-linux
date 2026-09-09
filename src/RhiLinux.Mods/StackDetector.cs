using System.Security.Cryptography;
using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed class StackDetector(GameProfileCatalog? profiles = null, ProxyDiagnosticsService? proxyDiagnostics = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GameProfileCatalog profiles = profiles ?? new GameProfileCatalog();
    private readonly ProxyDiagnosticsService proxyDiagnostics = proxyDiagnostics ?? new ProxyDiagnosticsService(profiles);

    public Task<StackSnapshot> DetectAsync(
        SteamGame game,
        long generation = 0,
        CancellationToken cancellationToken = default) =>
        DetectAsync(game.ToDeploymentTarget(), generation, cancellationToken);

    public Task<StackSnapshot> DetectAsync(
        InstalledGame game,
        long generation = 0,
        CancellationToken cancellationToken = default) =>
        DetectAsync(game.ToDeploymentTarget(), generation, cancellationToken);

    public async Task<StackSnapshot> DetectAsync(
        DeploymentTarget game,
        long generation = 0,
        CancellationToken cancellationToken = default)
    {
        GameManifest manifest;
        try
        {
            manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            var unknown = CreateUnknownComponents($"The ownership manifest could not be verified: {exception.Message}");
            return new StackSnapshot(
                game.InstallId.Value, game.GameRoot, game.DeploymentDirectory, game.Executable, generation,
                StackLayoutKind.Invalid, null, null, null, null, null, false,
                SelectedArchitecture(game), OwnershipHealth.Unavailable, unknown,
                "Ownership metadata could not be verified.",
                SteamAppId: game.SteamAppId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var profile = await profiles.MatchAsync(game, cancellationToken);
        var proxy = await proxyDiagnostics.DiagnoseAsync(game, cancellationToken);
        var eligibility = OptiScalerEligibilityService.Evaluate(game, profile, proxy);
        var architecture = SelectedArchitecture(game);
        var rootFiles = Directory.Exists(game.DeploymentDirectory)
            ? Directory.EnumerateFiles(game.DeploymentDirectory, "*", SearchOption.TopDirectoryOnly).ToArray()
            : [];

        var layout = AnalyzeLayout(game, manifest, rootFiles, profile, eligibility, architecture, proxy.SelectedProxy);
        var reshade = EvaluateReShade(layout, game, manifest, profile);
        var reno = EvaluateRenoDx(layout, reshade, game, manifest, profile, proxy);
        var opti = EvaluateOptiScaler(layout, game, manifest, eligibility);
        var components = new[] { reshade, reno, opti };
        var ownership = AggregateOwnership(components);
        var stackLayout = DetermineLayout(reshade, reno, opti, layout);
        var summary = BuildSummary(stackLayout, components, ownership);
        var launch = layout.ActiveProxy is not null
            ? DeploymentPlanner.GenerateLaunchOption(game, layout.ActiveProxy, layout.RenoDxFiles.Length > 0)
            : null;
        var defects = components
            .Where(x => x.State is ComponentLifecycleState.RepairRequired or ComponentLifecycleState.Conflict)
            .Select(x => x.Evidence.RepairReason ?? x.Explanation)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var warnings = components
            .Where(x => x.State is ComponentLifecycleState.InstalledWithWarnings or
                ComponentLifecycleState.InstalledMetadataIncomplete or ComponentLifecycleState.RepairRecommended)
            .Select(x => x.Diagnostic ?? x.Explanation)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var chainMode = layout.ProxyOwner == ComponentKind.OptiScaler && layout.HasReShadeForChaining
            ? (layout.ChainingConfigured ? "optiscaler-reshade64" : "optiscaler-reshade64-unconfigured")
            : layout.ProxyOwner == ComponentKind.ReShade ? "reshade-direct" : null;
        var requiredKeys = layout.ProxyOwner == ComponentKind.OptiScaler && layout.HasReShadeForChaining
            ? new[] { "LoadReshade=true", "LoadAsiPlugins=true" }
            : Array.Empty<string>();
        return new StackSnapshot(
            game.InstallId.Value, game.GameRoot, game.DeploymentDirectory, game.Executable, generation,
            stackLayout, layout.ActiveProxy, layout.ProxyOwner, layout.ReShadeRuntimePath,
            layout.RenoDxAddonPath, layout.OptiScalerRuntimePath, layout.ChainingConfigured,
            architecture, ownership, components, summary, launch, chainMode,
            UpdateEvaluator.BuildFingerprint(reshade),
            UpdateEvaluator.BuildFingerprint(reno),
            UpdateEvaluator.BuildFingerprint(opti),
            requiredKeys, defects, warnings,
            SteamAppId: game.SteamAppId);
    }

    public static ComponentStatus ToComponentStatus(ComponentStateReport report) =>
        new(
            report.Component,
            MapHealth(report),
            report.Version,
            report.Evidence.DetectedFiles.Select(Path.GetFileName).OfType<string>().ToArray(),
            report.Explanation,
            MapVerification(report),
            report.Diagnostic,
            report.State,
            report.Evidence.RepairReason,
            report.Evidence.ReasonCode,
            report.Runtime,
            report.Ownership,
            report.Update);

    public static IReadOnlyList<ComponentStatus> ToComponentStatuses(StackSnapshot snapshot) =>
        snapshot.Components.Select(ToComponentStatus).ToArray();

    private static ComponentHealth MapHealth(ComponentStateReport report) => report.State switch
    {
        ComponentLifecycleState.NotInstalled when report.Compatibility == CompatibilityStatus.Experimental =>
            ComponentHealth.Experimental,
        ComponentLifecycleState.NotInstalled when IsUnavailableExplanation(report.Explanation) =>
            ComponentHealth.Unavailable,
        ComponentLifecycleState.NotInstalled when IsSupportedExplanation(report.Explanation) =>
            ComponentHealth.Supported,
        ComponentLifecycleState.NotInstalled when report.Evidence.AvailableArtifactIdentity is not null &&
            report.Compatibility == CompatibilityStatus.Compatible =>
            ComponentHealth.DownloadRequired,
        ComponentLifecycleState.NotInstalled => ComponentHealth.Available,
        ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings or
            ComponentLifecycleState.InstalledMetadataIncomplete => ComponentHealth.Installed,
        ComponentLifecycleState.InstalledUnmanaged when report.Ownership == OwnershipHealth.Foreign =>
            ComponentHealth.ForeignInstallation,
        ComponentLifecycleState.InstalledUnmanaged => ComponentHealth.Installed,
        ComponentLifecycleState.UpdateAvailable => ComponentHealth.Outdated,
        ComponentLifecycleState.RepairRequired when report.FileIntegrity == FileIntegrityStatus.Incomplete =>
            ComponentHealth.PartiallyInstalled,
        ComponentLifecycleState.RepairRequired when report.FileIntegrity == FileIntegrityStatus.WrongArchitecture =>
            ComponentHealth.IncorrectlyConfigured,
        ComponentLifecycleState.RepairRequired when report.Runtime == RuntimeHealth.Broken &&
            report.FileIntegrity == FileIntegrityStatus.Corrupt =>
            ComponentHealth.Broken,
        ComponentLifecycleState.RepairRequired or ComponentLifecycleState.RepairRecommended =>
            ComponentHealth.IncorrectlyConfigured,
        ComponentLifecycleState.Conflict when report.Evidence.ReasonCode == "renodx.missing_proxy" =>
            ComponentHealth.MissingDependency,
        ComponentLifecycleState.Conflict => ComponentHealth.Conflicting,
        ComponentLifecycleState.Unsupported => ComponentHealth.Unsupported,
        ComponentLifecycleState.Unknown when report.Ownership == OwnershipHealth.Unavailable =>
            ComponentHealth.ManifestUnavailable,
        ComponentLifecycleState.Unknown => ComponentHealth.ForeignInstallation,
        _ => ComponentHealth.Available
    };

    private static bool IsUnavailableExplanation(string explanation) =>
        explanation.Contains("catalog unavailable", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("No RenoDX addon found", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("Not listed", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("Offline RenoDX catalog", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedExplanation(string explanation) =>
        explanation.Contains("manual download", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("No direct addon download", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("require confirmation", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("another executable", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("Architecture could not be confirmed", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("no compatible executable", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("artifact rejected", StringComparison.OrdinalIgnoreCase) ||
        explanation.Contains("superseded by a generic", StringComparison.OrdinalIgnoreCase);

    private static InstallationVerification MapVerification(ComponentStateReport report) => report.State switch
    {
        ComponentLifecycleState.RepairRequired => InstallationVerification.RepairNeeded,
        ComponentLifecycleState.InstalledUnmanaged => InstallationVerification.RecognizedExisting,
        ComponentLifecycleState.InstalledMetadataIncomplete => InstallationVerification.MetadataUnverified,
        ComponentLifecycleState.InstalledWithWarnings when report.Ownership is OwnershipHealth.Incomplete or OwnershipHealth.Migrated =>
            InstallationVerification.OptionalCleanupAvailable,
        ComponentLifecycleState.InstalledHealthy when report.Ownership == OwnershipHealth.Managed =>
            InstallationVerification.Managed,
        ComponentLifecycleState.InstalledHealthy => InstallationVerification.Managed,
        ComponentLifecycleState.UpdateAvailable when report.Ownership == OwnershipHealth.Managed =>
            InstallationVerification.Managed,
        ComponentLifecycleState.UpdateAvailable when report.Ownership == OwnershipHealth.Incomplete =>
            InstallationVerification.MetadataUnverified,
        ComponentLifecycleState.UpdateAvailable when report.Ownership is OwnershipHealth.Unmanaged or OwnershipHealth.Foreign =>
            InstallationVerification.RecognizedExisting,
        ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings or
            ComponentLifecycleState.InstalledMetadataIncomplete when report.Ownership is OwnershipHealth.Unmanaged or OwnershipHealth.Foreign =>
            InstallationVerification.RecognizedExisting,
        _ => InstallationVerification.None
    };

    private sealed record LayoutFacts(
        string? ActiveProxy,
        ComponentKind? ProxyOwner,
        string? ReShadeRuntimePath,
        string? RenoDxAddonPath,
        string? OptiScalerRuntimePath,
        bool ChainingConfigured,
        bool ExpectsReShadeChaining,
        bool HasReShadeForChaining,
        bool RecoverableCoexist,
        bool StrandedManagedCoexist,
        bool ForeignCoexist,
        bool CoexistConfigured,
        string[] RootFiles,
        string[] ReShadeFiles,
        string[] OptiScalerFiles,
        string[] RenoDxFiles,
        string[] ManagedReShade,
        string[] ManagedOpti,
        string[] ManagedReno,
        ManagedFile[] OptiPatcherRecords,
        ManagedFile[] InvalidPatcherRecords,
        bool ManifestMatchesGame,
        bool MetadataMigrated,
        bool OptiIniMalformed,
        bool OptiIniUserChanged,
        PeArchitecture SelectedArchitecture);

    private LayoutFacts AnalyzeLayout(
        DeploymentTarget game,
        GameManifest manifest,
        string[] rootFiles,
        GameProfileMatch profile,
        OptiScalerEligibility eligibility,
        PeArchitecture architecture,
        string? preferredProxyName)
    {
        var managedOptiProxies = ExistingManaged(manifest, game, ComponentKind.OptiScaler)
            .Where(x => DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var managedOptiProxy = managedOptiProxies.FirstOrDefault();
        var managedReShade = ExistingManaged(manifest, game, ComponentKind.ReShade);
        var coexistPath = Path.Combine(game.DeploymentDirectory, "ReShade64.dll");
        var managedCoexist = managedReShade.FirstOrDefault(x =>
            Path.GetFullPath(x).Equals(Path.GetFullPath(coexistPath), StringComparison.Ordinal));
        var coexistExists = File.Exists(coexistPath);
        var missingReShadeHashes = manifest.Files
            .Where(x => x.Component == ComponentKind.ReShade && !File.Exists(Path.Combine(game.GameRoot, x.RelativePath)))
            .Select(x => x.Sha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recoverableCoexist = coexistExists && managedCoexist is null && missingReShadeHashes.Count > 0 &&
            missingReShadeHashes.Contains(Hash(coexistPath));
        var iniPath = Path.Combine(game.DeploymentDirectory, "OptiScaler.ini");
        var iniExists = File.Exists(iniPath);
        var iniReadable = TryReadOptiScalerPluginFlags(iniPath, out var loadReShade, out var loadAsiPlugins);
        var coexistConfigured = iniReadable && loadReShade == true && loadAsiPlugins == true;
        var optiIniMalformed = iniExists && !iniReadable;
        var optiIniUserChanged = false;
        var runtimeIdentities = new Dictionary<string, (bool ReShade, bool OptiScaler)>(StringComparer.Ordinal);

        (bool ReShade, bool OptiScaler) RuntimeIdentity(string path)
        {
            if (runtimeIdentities.TryGetValue(path, out var cached)) return cached;
            if (!File.Exists(path)) return runtimeIdentities[path] = (false, false);
            var markers = BinaryMarkerScanner.ContainsEach(path,
                [["ReShade", "reshade.me"], ["OptiScaler", "OptiFG"]]);
            return runtimeIdentities[path] = (markers[0] && !markers[1], markers[1]);
        }

        var reshadeFiles = rootFiles.Where(x =>
                DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase) &&
                RuntimeIdentity(x).ReShade)
            .Concat(coexistExists && RuntimeIdentity(coexistPath).ReShade ? [coexistPath] : Array.Empty<string>())
            .Concat(managedReShade.Where(File.Exists))
            .Distinct(StringComparer.Ordinal).ToArray();
        var optiFiles = rootFiles.Where(x =>
                DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase) &&
                RuntimeIdentity(x).OptiScaler)
            .Concat(managedOptiProxies.Where(path =>
                File.Exists(path) && (RuntimeIdentity(path).OptiScaler || IsOwnedOptiScalerProxy(manifest, game, path))))
            .Distinct(StringComparer.Ordinal).ToArray();
        var renoFiles = ExistingManaged(manifest, game, ComponentKind.RenoDx)
            .Concat(rootFiles.Where(IsRenoDx))
            .Distinct(StringComparer.Ordinal).ToArray();

        var activeOpti = SelectActiveOptiScalerProxy(optiFiles, managedOptiProxies, preferredProxyName);
        var activeReShade = reshadeFiles.FirstOrDefault(x =>
            DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase));
        var activeProxy = activeOpti ?? activeReShade;
        var proxyOwner = activeOpti is not null ? ComponentKind.OptiScaler :
            activeReShade is not null ? ComponentKind.ReShade : (ComponentKind?)null;
        var reshadeRuntime = coexistExists &&
            (RuntimeIdentity(coexistPath).ReShade || managedCoexist is not null)
            ? coexistPath
            : activeReShade;
        var expectsChaining = managedCoexist is not null || manifest.Files.Any(file =>
            file.Component == ComponentKind.ReShade &&
            Path.GetFileName(file.RelativePath).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
        var hasChaining = coexistExists &&
            (RuntimeIdentity(coexistPath).ReShade || managedCoexist is not null ||
             reshadeFiles.Any(path => Path.GetFullPath(path).Equals(Path.GetFullPath(coexistPath), StringComparison.Ordinal)));
        var stranded = coexistExists && managedCoexist is not null && managedOptiProxy is null &&
            optiFiles.Length == 0;
        var foreignCoexist = coexistExists && managedCoexist is null &&
            (optiFiles.Length == 0 || !coexistConfigured) && !recoverableCoexist;

        if (activeOpti is not null && hasChaining && iniReadable)
            optiIniUserChanged = loadReShade != true || loadAsiPlugins != true;
        else if (activeOpti is not null && !hasChaining && iniReadable)
            optiIniUserChanged = !IsDisabledOrAutomatic(loadReShade) || !IsDisabledOrAutomatic(loadAsiPlugins);

        var patcherRecords = manifest.Files.Where(x => x.Component == ComponentKind.OptiPatcher).ToArray();
        var invalidPatcher = patcherRecords.Where(x => !ManagedFileMatches(game, x)).ToArray();
        _ = eligibility;
        _ = profile;

        return new LayoutFacts(
            activeProxy is null ? null : Path.GetFileName(activeProxy),
            proxyOwner,
            reshadeRuntime,
            renoFiles.FirstOrDefault(),
            activeOpti,
            coexistConfigured,
            expectsChaining,
            hasChaining,
            recoverableCoexist,
            stranded,
            foreignCoexist,
            coexistConfigured,
            rootFiles,
            reshadeFiles,
            optiFiles,
            renoFiles,
            managedReShade,
            ExistingManaged(manifest, game, ComponentKind.OptiScaler),
            ExistingManaged(manifest, game, ComponentKind.RenoDx),
            patcherRecords,
            invalidPatcher,
            manifest.Files.Count == 0 || ManifestBelongsToGame(manifest, game),
            manifest.MetadataMigrated,
            optiIniMalformed,
            optiIniUserChanged,
            architecture);
    }

    private static ComponentStateReport EvaluateReShade(
        LayoutFacts layout,
        DeploymentTarget game,
        GameManifest manifest,
        GameProfileMatch profile)
    {
        var evidence = BaseEvidence(ComponentKind.ReShade, layout.ReShadeFiles, layout.ManagedReShade, layout.ActiveProxy, layout.ProxyOwner, layout.SelectedArchitecture);
        if (!layout.ManifestMatchesGame && manifest.Files.Any(x => x.Component == ComponentKind.ReShade))
            return Report(ComponentKind.ReShade, ComponentLifecycleState.Unknown, RuntimeHealth.Unknown,
                FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Foreign,
                "The ownership manifest belongs to a different Steam AppID.", "ownership.foreign_appid", evidence);

        if (layout.ForeignCoexist && layout.ReShadeFiles.Length > 0 && layout.OptiScalerFiles.Length == 0)
            return Report(ComponentKind.ReShade, ComponentLifecycleState.InstalledUnmanaged, RuntimeHealth.Healthy,
                FileIntegrityStatus.Complete, ConfigurationHealth.Valid, OwnershipHealth.Foreign,
                "A foreign ReShade64.dll layout was detected. It will not be renamed, overwritten, or removed.",
                "reshade.foreign_coexist", evidence with { RepairReason = null });

        if (layout.RecoverableCoexist || layout.StrandedManagedCoexist)
            return Report(ComponentKind.ReShade, ComponentLifecycleState.RepairRequired, RuntimeHealth.Degraded,
                FileIntegrityStatus.Complete, ConfigurationHealth.Invalid, OwnershipHealth.Incomplete,
                layout.RecoverableCoexist
                    ? "A managed ReShade hash was found in the coexistence slot without OptiScaler."
                    : "Managed ReShade is stranded in the coexistence slot without a managed OptiScaler proxy.",
                "reshade.stranded_coexist",
                evidence with { RepairReason = "ReShade must be restored as the active proxy DLL." });

        if (layout.ManagedReShade.Length > 0 && layout.ProxyOwner == ComponentKind.OptiScaler &&
            layout.HasReShadeForChaining && !layout.CoexistConfigured)
            return Report(ComponentKind.ReShade, ComponentLifecycleState.RepairRecommended, RuntimeHealth.Degraded,
                FileIntegrityStatus.Complete, ConfigurationHealth.RecoverableIssues, OwnershipHealth.Managed,
                "Managed coexistence files exist, but OptiScaler is not configured to load ReShade.",
                "reshade.chaining_keys",
                evidence with { RepairReason = "OptiScaler.ini LoadReshade/LoadAsiPlugins keys need repair." });

        if (layout.ReShadeFiles.Any(path =>
        {
            var pe = ReadPeArchitecture(path);
            return pe is not PeArchitecture.Unknown && pe != layout.SelectedArchitecture &&
                   layout.SelectedArchitecture is not PeArchitecture.Unknown;
        }))
            return Report(ComponentKind.ReShade, ComponentLifecycleState.RepairRequired, RuntimeHealth.Broken,
                FileIntegrityStatus.WrongArchitecture, ConfigurationHealth.Valid, OwnershipHealth.Managed,
                "The managed ReShade architecture does not match the selected game executable.",
                "reshade.architecture_mismatch",
                evidence with { RepairReason = "ReShade architecture does not match the selected executable." });

        return FinalizeInstalledOrMissing(
            ComponentKind.ReShade, layout.ReShadeFiles, manifest, game, layout, evidence, profile);
    }

    private static ComponentStateReport EvaluateRenoDx(
        LayoutFacts layout,
        ComponentStateReport reshade,
        DeploymentTarget game,
        GameManifest manifest,
        GameProfileMatch profile,
        ProxySelectionResult proxy)
    {
        var evidence = BaseEvidence(ComponentKind.RenoDx, layout.RenoDxFiles, layout.ManagedReno, layout.ActiveProxy, layout.ProxyOwner, layout.SelectedArchitecture);
        if (!layout.ManifestMatchesGame && manifest.Files.Any(x => x.Component == ComponentKind.RenoDx))
            return Report(ComponentKind.RenoDx, ComponentLifecycleState.Unknown, RuntimeHealth.Unknown,
                FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Foreign,
                $"The ownership manifest belongs to install ID '{manifest.InstallId}' (Steam AppID {manifest.SteamAppId}), not the selected game. The installation is treated as unknown.",
                "ownership.foreign_appid", evidence);

        var unsafeRecords = manifest.Files.Where(x => x.Component == ComponentKind.RenoDx)
            .Where(x => !IsPathInsideGame(game, x.RelativePath)).ToArray();
        if (unsafeRecords.Length > 0)
            return Report(ComponentKind.RenoDx, ComponentLifecycleState.RepairRequired, RuntimeHealth.Broken,
                FileIntegrityStatus.Corrupt, ConfigurationHealth.Unknown, OwnershipHealth.Unavailable,
                "The ownership manifest contains an invalid path outside the selected game.",
                "ownership.unsafe_path",
                evidence with { RepairReason = "Correct or clear the ownership manifest before repair." });

        var invalidReno = manifest.Files.Where(file => file.Component == ComponentKind.RenoDx)
            .FirstOrDefault(file =>
            {
                var path = Path.Combine(game.GameRoot, file.RelativePath);
                if (!File.Exists(path)) return false;
                var provenanceName = RenoDxProvenanceFileName(file);
                var ownedName = Path.GetFileName(file.RelativePath);
                var expectedName = provenanceName ?? ownedName;
                var expectedArchitecture = layout.SelectedArchitecture is not PeArchitecture.Unknown
                    ? layout.SelectedArchitecture
                    : Path.GetExtension(expectedName).Equals(".addon32", StringComparison.OrdinalIgnoreCase)
                        ? PeArchitecture.X86
                        : Path.GetExtension(expectedName).Equals(".addon64", StringComparison.OrdinalIgnoreCase)
                            ? PeArchitecture.X64
                            : profile.Profile.RenoDx?.Architecture;
                return !HasExpectedRenoDxIdentity(path, expectedName, expectedArchitecture);
            });
        if (invalidReno is not null)
            return Report(ComponentKind.RenoDx, ComponentLifecycleState.RepairRequired, RuntimeHealth.Broken,
                FileIntegrityStatus.WrongArchitecture, ConfigurationHealth.Valid, OwnershipHealth.Managed,
                $"Managed RenoDX file '{Path.GetFileName(invalidReno.RelativePath)}' does not retain the expected addon filename or architecture.",
                "renodx.identity_mismatch",
                evidence with { RepairReason = $"RenoDX file '{Path.GetFileName(invalidReno.RelativePath)}' has the wrong filename or architecture." });

        if (manifest.Files.Any(file => file.Component == ComponentKind.RenoDx) && layout.RenoDxFiles.Length == 0)
            return Report(ComponentKind.RenoDx, ComponentLifecycleState.RepairRequired, RuntimeHealth.Broken,
                FileIntegrityStatus.Incomplete, ConfigurationHealth.Valid, OwnershipHealth.Incomplete,
                "The required RenoDX addon is missing.",
                "renodx.missing_addon",
                evidence with
                {
                    MissingRequiredFiles = ["*.addon64/*.addon32"],
                    RepairReason = "The required RenoDX addon file is missing."
                });

        if (layout.RenoDxFiles.Length > 0 &&
            reshade.State is not (ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings
                or ComponentLifecycleState.InstalledMetadataIncomplete or ComponentLifecycleState.UpdateAvailable
                or ComponentLifecycleState.RepairRecommended))
            return Report(ComponentKind.RenoDx, ComponentLifecycleState.RepairRequired, RuntimeHealth.Degraded,
                FileIntegrityStatus.Complete, ConfigurationHealth.Invalid, OwnershipFrom(layout, ComponentKind.RenoDx, manifest, game),
                "RenoDX is present, but its required full-addon ReShade host is not installed correctly.",
                "renodx.missing_host",
                evidence with { RepairReason = "Install or repair the ReShade host required by RenoDX." });

        if (layout.RenoDxFiles.Length == 0)
        {
            if (profile.Profile.RenoDx is null)
                return Report(ComponentKind.RenoDx, ComponentLifecycleState.Unsupported, RuntimeHealth.Unknown,
                    FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unknown,
                    profile.MatchReason, "renodx.unsupported", evidence);
            if (!proxy.HasSafeProxy)
                return Report(ComponentKind.RenoDx, ComponentLifecycleState.Conflict, RuntimeHealth.Unknown,
                    FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unknown,
                    $"Full-addon ReShade has no safe active proxy: {proxy.Reason}",
                    "renodx.missing_proxy", evidence, compatibility: CompatibilityStatus.Conflict);
            return Report(ComponentKind.RenoDx, ComponentLifecycleState.NotInstalled, RuntimeHealth.Unknown,
                FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unknown,
                profile.Profile.RenoDx!.IsGameSpecific
                    ? "The official game-specific RenoDX addon and full-addon ReShade can be installed automatically."
                    : $"The approved {profile.Profile.Engine} RenoDX fallback and full-addon ReShade can be installed automatically.",
                "renodx.not_installed", evidence, compatibility: CompatibilityStatus.Compatible);
        }

        return FinalizeInstalledOrMissing(
            ComponentKind.RenoDx, layout.RenoDxFiles, manifest, game, layout, evidence, profile);
    }

    private static ComponentStateReport EvaluateOptiScaler(
        LayoutFacts layout,
        DeploymentTarget game,
        GameManifest manifest,
        OptiScalerEligibility eligibility)
    {
        var evidence = BaseEvidence(ComponentKind.OptiScaler, layout.OptiScalerFiles, layout.ManagedOpti, layout.ActiveProxy, layout.ProxyOwner, layout.SelectedArchitecture);
        if (!layout.ManifestMatchesGame && manifest.Files.Any(x => x.Component == ComponentKind.OptiScaler))
            return Report(ComponentKind.OptiScaler, ComponentLifecycleState.Unknown, RuntimeHealth.Unknown,
                FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Foreign,
                "The ownership manifest belongs to a different Steam AppID.", "ownership.foreign_appid", evidence);

        var hasOwnership = manifest.Files.Any(file => file.Component == ComponentKind.OptiScaler);
        var recognized = layout.OptiScalerFiles.FirstOrDefault(path =>
            DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            ?? layout.OptiScalerRuntimePath;
        var acceptedProxies = layout.OptiScalerFiles
            .Where(path => DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hasOwnership && recognized is null)
        {
            var guessed = layout.ManagedOpti
                .Select(Path.GetFileName)
                .OfType<string>()
                .FirstOrDefault(name => DeploymentPlanner.SupportedProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase));
            return Report(ComponentKind.OptiScaler, ComponentLifecycleState.RepairRequired, RuntimeHealth.Broken,
                FileIntegrityStatus.Incomplete, ConfigurationHealth.Unknown, OwnershipHealth.Incomplete,
                "No accepted OptiScaler proxy DLL is present in the deployment directory.",
                "optiscaler.missing_proxy",
                evidence with
                {
                    ExpectedFiles = OptiScalerLayoutCatalog.AcceptedProxyNames,
                    MissingRequiredFiles = guessed is null ? ["proxy DLL"] : [guessed],
                    RepairReason = "The OptiScaler proxy DLL is missing."
                },
                diagnostic: guessed is null
                    ? "Accepted proxy candidates were scanned; none were present."
                    : $"Guessed ownership proxy '{guessed}' is absent. Accepted candidates: {string.Join(", ", OptiScalerLayoutCatalog.AcceptedProxyNames)}.");
        }

        if (recognized is not null && layout.ExpectsReShadeChaining && !layout.HasReShadeForChaining)
            return Report(ComponentKind.OptiScaler, ComponentLifecycleState.RepairRequired, RuntimeHealth.Degraded,
                FileIntegrityStatus.Incomplete, ConfigurationHealth.Invalid, OwnershipHealth.Managed,
                "OptiScaler is active, but the required ReShade64.dll chaining file is missing.",
                "optiscaler.missing_reshade64",
                evidence with
                {
                    MissingRequiredFiles = ["ReShade64.dll"],
                    RepairReason = "ReShade64.dll is missing."
                });

        if (recognized is not null && layout.OptiIniMalformed)
            return Report(ComponentKind.OptiScaler, ComponentLifecycleState.RepairRequired, RuntimeHealth.Degraded,
                FileIntegrityStatus.Complete, ConfigurationHealth.Invalid, OwnershipHealth.Managed,
                "OptiScaler.ini is malformed and cannot be verified.",
                "optiscaler.ini_malformed",
                evidence with { RepairReason = "OptiScaler.ini cannot be parsed; managed chaining keys need repair." });

        if (recognized is null)
        {
            return eligibility.Level switch
            {
                OptiScalerCompatibilityLevel.Experimental =>
                    Report(ComponentKind.OptiScaler, ComponentLifecycleState.NotInstalled, RuntimeHealth.Unknown,
                        FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unknown,
                        eligibility.Explanation, "optiscaler.experimental", evidence,
                        compatibility: CompatibilityStatus.Experimental),
                OptiScalerCompatibilityLevel.Unsupported or OptiScalerCompatibilityLevel.BlockedByAntiCheat =>
                    Report(ComponentKind.OptiScaler, ComponentLifecycleState.Unsupported, RuntimeHealth.Unknown,
                        FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unknown,
                        eligibility.Explanation, "optiscaler.unsupported", evidence,
                        compatibility: CompatibilityStatus.Unsupported),
                OptiScalerCompatibilityLevel.BlockedByUnresolvedFileConflict =>
                    Report(ComponentKind.OptiScaler, ComponentLifecycleState.Conflict, RuntimeHealth.Unknown,
                        FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unknown,
                        eligibility.Explanation, "optiscaler.conflict", evidence,
                        compatibility: CompatibilityStatus.Conflict),
                _ => Report(ComponentKind.OptiScaler, ComponentLifecycleState.NotInstalled, RuntimeHealth.Unknown,
                    FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unknown,
                    eligibility.Explanation, "optiscaler.not_installed", evidence,
                    compatibility: CompatibilityStatus.Compatible)
            };
        }

        var layoutKind = OptiScalerLayoutCatalog.DetermineKind(
            layout.HasReShadeForChaining || layout.ReShadeFiles.Length > 0,
            layout.RenoDxFiles.Length > 0);
        var matchedLayout = OptiScalerLayoutCatalog.Create(
            layoutKind, game.DeploymentDirectory, layout.SelectedArchitecture,
            Path.GetFileName(recognized)!, layout.RenoDxAddonPath);
        var report = FinalizeInstalledOrMissing(
            ComponentKind.OptiScaler, layout.OptiScalerFiles, manifest, game, layout, evidence, null);
        if (report.State is ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledMetadataIncomplete
            or ComponentLifecycleState.InstalledWithWarnings or ComponentLifecycleState.InstalledUnmanaged)
        {
            var layoutDetail =
                $"Detected active proxy: {Path.GetFileName(recognized)}. Accepted candidates: {string.Join(", ", acceptedProxies.Length == 0 ? matchedLayout.AcceptedActiveProxies : acceptedProxies)}. Selected layout: {matchedLayout.Kind}.";
            report = report with
            {
                Explanation = report.State == ComponentLifecycleState.InstalledHealthy
                    ? $"Installed files match the {matchedLayout.Kind} layout with active proxy {Path.GetFileName(recognized)}."
                    : report.Explanation,
                Diagnostic = string.IsNullOrWhiteSpace(report.Diagnostic)
                    ? $"{layoutDetail} Layout is healthy."
                    : $"{report.Diagnostic} {layoutDetail}",
                Evidence = report.Evidence with
                {
                    ActiveProxy = Path.GetFileName(recognized),
                    ExpectedFiles = matchedLayout.AcceptedActiveProxies
                }
            };
            if (layout.OptiIniUserChanged)
            {
                report = report with
                {
                    Configuration = ConfigurationHealth.ValidWithUserChanges,
                    State = report.State == ComponentLifecycleState.InstalledHealthy
                        ? ComponentLifecycleState.InstalledMetadataIncomplete
                        : report.State,
                    Explanation = "OptiScaler is installed. Mutable configuration differs from defaults and does not require repair.",
                    Diagnostic = "User-editable OptiScaler.ini keys differ from defaults. Runtime files remain usable.",
                    Evidence = report.Evidence with { ReasonCode = "optiscaler.user_config" }
                };
            }

            if (layout.InvalidPatcherRecords.Length > 0 &&
                report.Ownership is OwnershipHealth.Managed or OwnershipHealth.Incomplete or OwnershipHealth.Migrated)
            {
                report = report with
                {
                    State = ComponentLifecycleState.InstalledWithWarnings,
                    Ownership = OwnershipHealth.Incomplete,
                    Diagnostic =
                        $"{layout.InvalidPatcherRecords.Length} legacy OptiPatcher ownership record(s) no longer match disk. OptiPatcher is not required by the current AMD layout; optional metadata cleanup is available.",
                    Evidence = report.Evidence with { ReasonCode = "optiscaler.optional_patcher_cleanup" }
                };
            }
        }

        return report;
    }

    private static ComponentStateReport FinalizeInstalledOrMissing(
        ComponentKind component,
        IReadOnlyList<string> detected,
        GameManifest manifest,
        DeploymentTarget game,
        LayoutFacts layout,
        ComponentStateEvidence evidence,
        GameProfileMatch? profile)
    {
        _ = profile;
        if (detected.Count == 0)
            return Report(component, ComponentLifecycleState.NotInstalled, RuntimeHealth.Unknown,
                FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unknown,
                "Not installed.", $"{component.ToString().ToLowerInvariant()}.not_installed", evidence);

        var recorded = manifest.Files.Where(x => x.Component == component).ToArray();
        var unsafeRecords = recorded.Where(x => !IsPathInsideGame(game, x.RelativePath)).ToArray();
        if (unsafeRecords.Length > 0)
            return Report(component, ComponentLifecycleState.RepairRequired, RuntimeHealth.Broken,
                FileIntegrityStatus.Corrupt, ConfigurationHealth.Unknown, OwnershipHealth.Unavailable,
                "The ownership manifest contains an invalid path outside the selected game.",
                "ownership.unsafe_path",
                evidence with { RepairReason = "Correct or clear the ownership manifest before repair." });

        var missing = recorded.Where(x => !File.Exists(Path.Combine(game.GameRoot, x.RelativePath))).ToArray();
        var requiredMissing = missing.Where(x => IsRequiredImmutableRuntime(x, component)).ToArray();
        if (component == ComponentKind.OptiScaler)
        {
            var hasActiveProxy = detected.Any(path =>
                DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) &&
                (IsRecognizedRuntime(path, ComponentKind.OptiScaler) || File.Exists(path)));
            if (hasActiveProxy)
            {
                requiredMissing = requiredMissing.Where(file =>
                {
                    var name = Path.GetFileName(file.RelativePath);
                    if (!DeploymentPlanner.SupportedProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        return true;
                    return !detected.Any(path =>
                        DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));
                }).ToArray();
            }
        }
        var changedImmutable = recorded.Where(x =>
        {
            if (!IsImmutableRuntimeRecord(x)) return false;
            var path = Path.Combine(game.GameRoot, x.RelativePath);
            return File.Exists(path) && !Hash(path).Equals(x.Sha256, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        var unrecognizedImmutable = changedImmutable
            .Where(x => IsRequiredImmutableRuntime(x, component) &&
                        !IsRecognizedRuntime(Path.Combine(game.GameRoot, x.RelativePath), component))
            .ToArray();
        var changedMutable = recorded.Where(x =>
        {
            if (IsImmutableRuntimeRecord(x)) return false;
            var path = Path.Combine(game.GameRoot, x.RelativePath);
            return File.Exists(path) && !Hash(path).Equals(x.Sha256, StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        if (requiredMissing.Length > 0 || unrecognizedImmutable.Length > 0)
        {
            var missingNames = requiredMissing.Select(x => Path.GetFileName(x.RelativePath))
                .Concat(unrecognizedImmutable.Select(x => Path.GetFileName(x.RelativePath)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return Report(component, ComponentLifecycleState.RepairRequired, RuntimeHealth.Broken,
                FileIntegrityStatus.Incomplete, ConfigurationHealth.Unknown, OwnershipHealth.Incomplete,
                $"Required runtime files are missing or no longer recognized: {string.Join(", ", missingNames)}.",
                $"{component.ToString().ToLowerInvariant()}.runtime_broken",
                evidence with
                {
                    MissingRequiredFiles = missingNames,
                    RepairReason = $"{missingNames[0]} is missing or is not a recognized {component} runtime."
                });
        }

        var version = SelectRuntimeArtifactVersion(recorded, detected, component);
        var ownership = recorded.Length == 0 ? OwnershipHealth.Unmanaged :
            layout.MetadataMigrated ? OwnershipHealth.Migrated :
            missing.Length > 0 || changedImmutable.Length > 0 || changedMutable.Length > 0
                ? OwnershipHealth.Incomplete
                : OwnershipHealth.Managed;
        var state = ownership switch
        {
            OwnershipHealth.Unmanaged => ComponentLifecycleState.InstalledUnmanaged,
            OwnershipHealth.Incomplete or OwnershipHealth.Migrated => ComponentLifecycleState.InstalledMetadataIncomplete,
            _ => ComponentLifecycleState.InstalledHealthy
        };
        var diagnostic = ownership switch
        {
            OwnershipHealth.Unmanaged =>
                "Runtime files are recognized without an RHI Linux ownership record. Removal moves only recognized component files to recovery storage.",
            OwnershipHealth.Incomplete or OwnershipHealth.Migrated =>
                $"Runtime verification succeeded. Ownership metadata differs from disk ({missing.Length} missing record(s), {changedImmutable.Length} immutable hash change(s), {changedMutable.Length} mutable configuration change(s)){(layout.MetadataMigrated ? "; legacy schema was migrated in memory" : string.Empty)}. Mutable configuration drift does not require repair.",
            _ => null
        };
        var orderedEvidence = PreferRuntimeEvidence(detected, component, evidence);
        return Report(component, state, RuntimeHealth.Healthy, FileIntegrityStatus.Complete,
            changedMutable.Length > 0 ? ConfigurationHealth.ValidWithUserChanges : ConfigurationHealth.Valid,
            ownership,
            state == ComponentLifecycleState.InstalledHealthy
                ? "Installed files form a recognized runtime layout."
                : "Installed files are ready. Ownership metadata differs from disk and does not require repair.",
            $"{component.ToString().ToLowerInvariant()}.{state.ToString().ToLowerInvariant()}",
            orderedEvidence with
            {
                InstalledArtifactIdentity = version,
                MissingRequiredFiles = []
            },
            version: version,
            diagnostic: diagnostic);
    }

    internal static string? SelectRuntimeArtifactVersion(
        IReadOnlyList<ManagedFile> recorded,
        IReadOnlyList<string> detected,
        ComponentKind component)
    {
        foreach (var file in recorded.Where(IsImmutableRuntimeRecord))
        {
            if (string.IsNullOrWhiteSpace(file.Version) || IsConfigurationSchemaVersion(file.Version))
                continue;
            var path = detected.FirstOrDefault(candidate =>
                Path.GetFileName(candidate).Equals(Path.GetFileName(file.RelativePath), StringComparison.OrdinalIgnoreCase));
            if (path is not null && !IsRecognizedRuntime(path, component) &&
                !IsImmutableRuntimeFileName(file.RelativePath, component))
                continue;
            return file.Version;
        }

        foreach (var path in PreferRuntimePaths(detected, component))
        {
            var match = recorded.FirstOrDefault(file =>
                Path.GetFileName(file.RelativePath).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(file.Version) &&
                !IsConfigurationSchemaVersion(file.Version));
            if (match?.Version is { } version) return version;
        }

        return recorded
            .Where(IsImmutableRuntimeRecord)
            .Select(x => x.Version)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version) && !IsConfigurationSchemaVersion(version));
    }

    internal static bool IsConfigurationSchemaVersion(string? version) =>
        version is not null &&
        (version.Equals("1", StringComparison.Ordinal) ||
         version.Equals("0", StringComparison.Ordinal) ||
         version.StartsWith("reshade-clean-", StringComparison.OrdinalIgnoreCase) ||
         version.Equals("plugins-v1", StringComparison.OrdinalIgnoreCase));

    private static ComponentStateEvidence PreferRuntimeEvidence(
        IReadOnlyList<string> detected,
        ComponentKind component,
        ComponentStateEvidence evidence) =>
        evidence with { DetectedFiles = PreferRuntimePaths(detected, component).ToArray() };

    private static IEnumerable<string> PreferRuntimePaths(IReadOnlyList<string> detected, ComponentKind component)
    {
        foreach (var path in detected.Where(path => IsRecognizedRuntime(path, component)))
            yield return path;
        foreach (var path in detected.Where(path => !IsRecognizedRuntime(path, component)))
            yield return path;
    }

    private static bool IsImmutableRuntimeFileName(string relativePath, ComponentKind component)
    {
        var name = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(name);
        return component switch
        {
            ComponentKind.ReShade => extension.Equals(".dll", StringComparison.OrdinalIgnoreCase),
            ComponentKind.OptiScaler => extension.Equals(".dll", StringComparison.OrdinalIgnoreCase),
            ComponentKind.RenoDx => extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static OwnershipHealth OwnershipFrom(
        LayoutFacts layout,
        ComponentKind component,
        GameManifest manifest,
        DeploymentTarget game)
    {
        var recorded = manifest.Files.Count(x => x.Component == component);
        if (recorded == 0) return OwnershipHealth.Unmanaged;
        if (!layout.ManifestMatchesGame) return OwnershipHealth.Foreign;
        if (layout.MetadataMigrated) return OwnershipHealth.Migrated;
        return OwnershipHealth.Managed;
    }

    private static ComponentStateEvidence BaseEvidence(
        ComponentKind component,
        IReadOnlyList<string> detected,
        IReadOnlyList<string> managed,
        string? activeProxy,
        ComponentKind? proxyOwner,
        PeArchitecture architecture) =>
        new(detected, managed.Select(Path.GetFileName).OfType<string>().ToArray(), [], [],
            activeProxy, proxyOwner, architecture, null, null, null,
            $"{component.ToString().ToLowerInvariant()}.evaluated");

    private static ComponentStateReport Report(
        ComponentKind component,
        ComponentLifecycleState state,
        RuntimeHealth runtime,
        FileIntegrityStatus integrity,
        ConfigurationHealth configuration,
        OwnershipHealth ownership,
        string explanation,
        string reasonCode,
        ComponentStateEvidence evidence,
        string? version = null,
        string? diagnostic = null,
        CompatibilityStatus compatibility = CompatibilityStatus.Unknown,
        UpdateAvailability update = UpdateAvailability.Unknown) =>
        new(component, state, runtime, integrity, configuration, ownership, update, compatibility,
            version, explanation, diagnostic, evidence with { ReasonCode = reasonCode });

    private static StackLayoutKind DetermineLayout(
        ComponentStateReport reshade,
        ComponentStateReport reno,
        ComponentStateReport opti,
        LayoutFacts layout)
    {
        bool Present(ComponentStateReport report) => report.State is
            ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings or
            ComponentLifecycleState.InstalledMetadataIncomplete or ComponentLifecycleState.InstalledUnmanaged or
            ComponentLifecycleState.UpdateAvailable or ComponentLifecycleState.RepairRecommended or
            ComponentLifecycleState.RepairRequired;
        var hasReShade = Present(reshade) || layout.ReShadeFiles.Length > 0;
        var hasReno = Present(reno) || layout.RenoDxFiles.Length > 0;
        var hasOpti = Present(opti) || layout.OptiScalerFiles.Length > 0;
        if (hasOpti && hasReShade && hasReno) return StackLayoutKind.FullStack;
        if (hasOpti && hasReno) return StackLayoutKind.OptiScalerRenoDx;
        if (hasOpti && hasReShade) return StackLayoutKind.OptiScalerReShade;
        if (hasOpti) return StackLayoutKind.OptiScalerOnly;
        if (hasReShade && hasReno) return StackLayoutKind.ReShadeRenoDx;
        if (hasReShade) return StackLayoutKind.ReShadeOnly;
        return StackLayoutKind.Empty;
    }

    private static OwnershipHealth AggregateOwnership(IReadOnlyList<ComponentStateReport> components)
    {
        if (components.Any(x => x.Ownership == OwnershipHealth.Unavailable)) return OwnershipHealth.Unavailable;
        if (components.Any(x => x.Ownership == OwnershipHealth.Foreign)) return OwnershipHealth.Foreign;
        if (components.Any(x => x.Ownership is OwnershipHealth.Incomplete or OwnershipHealth.Migrated))
            return OwnershipHealth.Incomplete;
        if (components.Any(x => x.Ownership == OwnershipHealth.Managed)) return OwnershipHealth.Managed;
        if (components.Any(x => x.Ownership == OwnershipHealth.Unmanaged)) return OwnershipHealth.Unmanaged;
        return OwnershipHealth.Unknown;
    }

    private static string BuildSummary(
        StackLayoutKind layout,
        IReadOnlyList<ComponentStateReport> components,
        OwnershipHealth ownership)
    {
        if (ownership == OwnershipHealth.Unavailable) return "Ownership metadata could not be verified.";
        if (components.Any(x => x.State == ComponentLifecycleState.RepairRequired))
            return components.First(x => x.State == ComponentLifecycleState.RepairRequired).Evidence.RepairReason
                ?? "Repair the managed installation.";
        if (components.Any(x => x.State == ComponentLifecycleState.UpdateAvailable))
            return "An update is available for installed components.";
        if (components.Any(x => x.State is ComponentLifecycleState.InstalledHealthy or
                ComponentLifecycleState.InstalledWithWarnings or ComponentLifecycleState.InstalledMetadataIncomplete))
            return $"Detected stack: {layout}.";
        return "The recommended setup can be installed.";
    }

    private static IReadOnlyList<ComponentStateReport> CreateUnknownComponents(string explanation) =>
        new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler }
            .Select(component => Report(component, ComponentLifecycleState.Unknown, RuntimeHealth.Unknown,
                FileIntegrityStatus.Unknown, ConfigurationHealth.Unknown, OwnershipHealth.Unavailable,
                explanation, "ownership.unavailable",
                new ComponentStateEvidence([], [], [], [], null, null, PeArchitecture.Unknown, null, null, null,
                    "ownership.unavailable")))
            .ToArray();

    private static string[] ExistingManaged(GameManifest manifest, DeploymentTarget game, ComponentKind component) =>
        manifest.Files
            .Where(x => x.Component == component)
            .Where(x => IsPathInsideGame(game, x.RelativePath))
            .Select(x => Path.Combine(game.GameRoot, x.RelativePath))
            .Where(File.Exists).ToArray();

    internal static bool IsRecognizedRuntime(string path, ComponentKind component)
    {
        if (!File.Exists(path)) return false;
        switch (component)
        {
            case ComponentKind.OptiScaler:
                return BinaryMarkerScanner.ContainsAny(path, "OptiScaler", "OptiFG");
            case ComponentKind.ReShade:
                var markers = BinaryMarkerScanner.ContainsEach(path,
                    [["ReShade", "reshade.me"], ["OptiScaler", "OptiFG"]]);
                return markers[0] && !markers[1];
            case ComponentKind.RenoDx:
                return IsRenoDx(path);
            case ComponentKind.OptiPatcher:
                return Path.GetExtension(path).Equals(".asi", StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static string? SelectActiveOptiScalerProxy(
        IReadOnlyList<string> optiFiles,
        IReadOnlyList<string> managedProxies,
        string? preferredProxyName)
    {
        static string? MatchByName(IEnumerable<string> paths, string? name) =>
            name is null ? null : paths.FirstOrDefault(path =>
                Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));

        var preferred = MatchByName(optiFiles, preferredProxyName) ?? MatchByName(managedProxies, preferredProxyName);
        if (preferred is not null && File.Exists(preferred)) return preferred;

        var managed = managedProxies.FirstOrDefault(File.Exists);
        if (managed is not null) return managed;

        return optiFiles.FirstOrDefault(path =>
            DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) &&
            File.Exists(path));
    }

    private static bool IsOwnedOptiScalerProxy(GameManifest manifest, DeploymentTarget game, string path)
    {
        var relative = Path.GetRelativePath(game.GameRoot, path);
        var record = manifest.Files.FirstOrDefault(file =>
            file.Component == ComponentKind.OptiScaler &&
            file.RelativePath.Equals(relative, StringComparison.Ordinal));
        if (record is null) return false;
        if (!DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(record.BundleRelativePath) ||
            Path.GetFileName(record.BundleRelativePath).Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
            OptiScalerBundleParser.IsSupportedProxyName(Path.GetFileName(record.BundleRelativePath));
    }

    private static bool IsImmutableRuntimeRecord(ManagedFile file)
    {
        if (file.FileClass is ManagedFileClass.MutableConfiguration or ManagedFileClass.UserEditableConfiguration or
            ManagedFileClass.ManagedGeneratedFile or ManagedFileClass.Backup)
            return false;
        if (file.FileClass == ManagedFileClass.ImmutableRuntimeBinary) return true;
        var extension = Path.GetExtension(file.RelativePath);
        return !extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) &&
               !extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) &&
               !extension.Equals(".toml", StringComparison.OrdinalIgnoreCase) &&
               !extension.Equals(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRequiredImmutableRuntime(ManagedFile file, ComponentKind component)
    {
        if (!IsImmutableRuntimeRecord(file)) return false;
        if (file.Requirement is DeploymentFileRequirement.Optional or DeploymentFileRequirement.Conditional or
            DeploymentFileRequirement.Debug or DeploymentFileRequirement.Documentation or
            DeploymentFileRequirement.InstallerOnly or DeploymentFileRequirement.UninstallerMetadata or
            DeploymentFileRequirement.Unsupported or DeploymentFileRequirement.UnrelatedArchiveContent)
            return false;
        if (component is ComponentKind.OptiScaler or ComponentKind.OptiPatcher)
        {
            if (file.Requirement == DeploymentFileRequirement.RequiredForSelectedMode)
                return true;
            return OptiScalerBundleParser.RequiresRepairWhenMissing(file.BundleRelativePath ?? file.RelativePath);
        }

        return true;
    }

    private static bool IsRenoDx(string path) =>
        (Path.GetExtension(path).Equals(".addon64", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".addon32", StringComparison.OrdinalIgnoreCase)) &&
        Path.GetFileName(path).Contains("renodx", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadOptiScalerPluginFlags(string iniPath, out bool? loadReshade, out bool? loadAsiPlugins)
    {
        loadReshade = null;
        loadAsiPlugins = null;
        if (!File.Exists(iniPath)) return false;
        try
        {
            var ini = IniDocument.Parse(File.ReadAllBytes(iniPath));
            if (ini.HasFatalIssues) return false;
            var reshadeSection = ini.FindSectionContaining("LoadReshade");
            var asiSection = ini.FindSectionContaining("LoadAsiPlugins");
            if (ini.HasRecoverableIssues &&
                (reshadeSection is null || asiSection is null ||
                 reshadeSection.Length == 0 || asiSection.Length == 0))
                return false;
            if (reshadeSection is not null && ini.ValueCount(reshadeSection, "LoadReshade") != 1) return false;
            if (asiSection is not null && ini.ValueCount(asiSection, "LoadAsiPlugins") != 1) return false;
            loadReshade = ParseOptionalBool(reshadeSection is null ? null : ini.Get(reshadeSection, "LoadReshade"));
            loadAsiPlugins = ParseOptionalBool(asiSection is null ? null : ini.Get(asiSection, "LoadAsiPlugins"));
            return reshadeSection is not null || asiSection is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool? ParseOptionalBool(string? value)
    {
        if (value is null) return null;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static bool IsDisabledOrAutomatic(bool? value) => value is null or false;

    private static bool HasExpectedRenoDxIdentity(
        string path,
        string? expectedFileName,
        PeArchitecture? expectedArchitecture)
    {
        if (expectedFileName is not null &&
            !Path.GetFileName(path).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase)) return false;
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase)) return false;
        var extensionArchitecture = extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
            ? PeArchitecture.X86 : PeArchitecture.X64;
        var peArchitecture = ReadPeArchitecture(path);
        return (expectedArchitecture is null or PeArchitecture.Unknown || extensionArchitecture == expectedArchitecture) &&
            (peArchitecture == PeArchitecture.Unknown || peArchitecture == extensionArchitecture);
    }

    private static string? RenoDxProvenanceFileName(ManagedFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.BundleRelativePath))
        {
            var bundled = Path.GetFileName(file.BundleRelativePath);
            if (Path.GetExtension(bundled).StartsWith(".addon", StringComparison.OrdinalIgnoreCase)) return bundled;
        }
        if (Uri.TryCreate(file.SourceUrl, UriKind.Absolute, out var source))
        {
            var sourceName = Path.GetFileName(Uri.UnescapeDataString(source.AbsolutePath));
            if (Path.GetExtension(sourceName).StartsWith(".addon", StringComparison.OrdinalIgnoreCase)) return sourceName;
        }
        return null;
    }

    private static bool ManagedFileMatches(DeploymentTarget game, ManagedFile file)
    {
        if (!IsPathInsideGame(game, file.RelativePath)) return false;
        var path = Path.Combine(game.GameRoot, file.RelativePath);
        return File.Exists(path) && Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ManifestBelongsToGame(GameManifest manifest, DeploymentTarget game)
        => GameInstallId.MatchesStoredIdentity(
            manifest.InstallId, manifest.SteamAppId, game.InstallId, game.SteamAppId);

    private static bool IsPathInsideGame(DeploymentTarget game, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return false;
        var gameRoot = Path.GetFullPath(game.GameRoot);
        var candidate = Path.GetFullPath(Path.Combine(gameRoot, relativePath));
        return candidate.Equals(gameRoot, StringComparison.Ordinal) ||
            candidate.StartsWith(gameRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static PeArchitecture ReadPeArchitecture(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return PeArchitecture.Unknown;
            stream.Position = 0x3c;
            var offset = reader.ReadUInt32();
            if (offset > stream.Length - 6) return PeArchitecture.Unknown;
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550) return PeArchitecture.Unknown;
            return reader.ReadUInt16() switch
            {
                0x014c => PeArchitecture.X86,
                0x8664 => PeArchitecture.X64,
                0xaa64 => PeArchitecture.Arm64,
                _ => PeArchitecture.Unknown
            };
        }
        catch (IOException) { return PeArchitecture.Unknown; }
        catch (UnauthorizedAccessException) { return PeArchitecture.Unknown; }
    }

    private static PeArchitecture SelectedArchitecture(DeploymentTarget game) =>
        game.Candidates.FirstOrDefault(x => game.Executable is not null &&
            Path.GetFullPath(x.Path).Equals(Path.GetFullPath(game.Executable), StringComparison.Ordinal))?.Architecture ??
        (game.Executable is not null && File.Exists(game.Executable)
            ? ReadPeArchitecture(game.Executable)
            : PeArchitecture.Unknown);

    private static bool ContainsMarker(string path, string marker) =>
        BinaryMarkerScanner.ContainsAny(path, marker);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
