using System.Security.Cryptography;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed record ComponentArtifact(
    ComponentKind Component,
    string Path,
    string FileName,
    string? Version = null,
    string? SourceUrl = null,
    string? Sha256 = null,
    string? RelativePath = null,
    string? SourceBlobSha256 = null,
    string? SourceBundleSha256 = null,
    DeploymentFileRequirement Requirement = DeploymentFileRequirement.Required,
    string? RequirementReason = null,
    string? Feature = null,
    bool CanOmitOnCollision = false);

public sealed record RecommendedStackArtifacts(
    ComponentArtifact? ReShade,
    ComponentArtifact? RenoDx,
    ComponentArtifact? OptiScaler,
    IReadOnlyList<ComponentArtifact>? OptiScalerSupportFiles = null,
    OptiScalerBundleManifest? OptiScalerBundle = null);

public sealed class DeploymentPlanner(TargetFileClassifier? targetFileClassifier = null)
{
    private readonly TargetFileClassifier targetFileClassifier = targetFileClassifier ?? new TargetFileClassifier();
    public static readonly string[] SupportedProxyNames =
        ["dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll"];

    public async Task<DeploymentPlan> BuildRecommendedStackPlanAsync(
        SteamGame game,
        RecommendedStackArtifacts artifacts,
        ProxySelectionResult? proxySelection = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBundle(game, artifacts);
        var activeProxyArtifact = artifacts.OptiScaler ?? artifacts.ReShade;
        if (activeProxyArtifact is not null)
            proxySelection = await new ProxyDiagnosticsService().DiagnoseAsync(
                game, activeProxyArtifact.Component, [activeProxyArtifact], cancellationToken);
        else
            proxySelection ??= await new ProxyDiagnosticsService().DiagnoseAsync(game, cancellationToken);
        var proxyName = proxySelection.SelectedProxy ??
            throw BuildProxyConflict(proxySelection, game, activeProxyArtifact?.Component ?? ComponentKind.ReShade);
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
        EnsureManifestBelongsToGame(manifest, game);
        var profile = await new GameProfileCatalog().MatchAsync(game, cancellationToken);
        var requiresRepair = manifest.Files.Any(file => IsImmutableRuntimeDrift(game, file)) ||
            HasUnparseableManagedOptiScalerIni(game, manifest);
        var targetArtifacts = new[] { artifacts.ReShade, artifacts.RenoDx, artifacts.OptiScaler }.OfType<ComponentArtifact>();
        var hasVersionUpdate = targetArtifacts.Any(artifact => manifest.Files.Any(file =>
            file.Component == artifact.Component && file.Version is not null && artifact.Version is not null &&
            !file.Version.Equals(artifact.Version, StringComparison.OrdinalIgnoreCase)));
        var plan = NewPlan(game, requiresRepair ? "repair recommended setup" :
            hasVersionUpdate ? "update recommended setup" : "install recommended setup");
        if (requiresRepair)
        {
            plan.RequiresRepair = true;
            plan.CompatibilityMessage = "Some files from an earlier installation need to be repaired before continuing.";
        }
        var selectedSupportFiles = SelectOptiScalerSupportFiles(game, profile.Profile,
            artifacts.OptiScalerSupportFiles ?? []);
        var officialArtifacts = new[] { artifacts.ReShade, artifacts.RenoDx, artifacts.OptiScaler }
            .OfType<ComponentArtifact>().Concat(artifacts.OptiScalerSupportFiles ?? []).ToArray();
        AddCommonWarnings(plan, game, proxySelection);
        AddRejectedProxyDecisions(plan, proxySelection, proxyName,
            artifacts.OptiScaler is not null ? ComponentKind.OptiScaler : ComponentKind.ReShade);
        AddOmittedBundleDecisions(plan, game, artifacts.OptiScalerBundle, selectedSupportFiles);
        if (proxySelection.Candidates.Any(x => !x.SafeForNewInstallation && File.Exists(x.Path)))
            plan.CompatibilityMessage ??= "A safe compatibility filename is available without changing existing game files.";
        plan.Operations.Add(new(DeploymentOperationType.CreateDirectory, game.DeploymentDirectory));

        var proxy = Path.Combine(game.DeploymentDirectory, proxyName);
        var coexist = Path.Combine(game.DeploymentDirectory, "ReShade64.dll");
        var managedOptiProxy = ManagedAt(manifest, game, proxy, ComponentKind.OptiScaler) && File.Exists(proxy);
        var managedReShadeProxyRecord = ManagedRecordAt(manifest, game, proxy, ComponentKind.ReShade);
        var managedReShadeProxy = managedReShadeProxyRecord is not null && File.Exists(proxy) &&
            HashPath(proxy).Equals(managedReShadeProxyRecord.Sha256, StringComparison.OrdinalIgnoreCase);
        var changedManagedReShadeProxy = managedReShadeProxyRecord is not null && File.Exists(proxy) &&
            !managedReShadeProxy;
        var managedCoexist = ManagedAt(manifest, game, coexist, ComponentKind.ReShade) && File.Exists(coexist);
        var finalOpti = managedOptiProxy || artifacts.OptiScaler is not null;
        var finalReShade = managedReShadeProxy || managedCoexist || artifacts.ReShade is not null;

        if (finalOpti)
        {
            if (changedManagedReShadeProxy && artifacts.ReShade is null)
                throw new DeploymentConflictException(
                    "The managed ReShade proxy changed after installation. Supply a validated ReShade artifact so it can be repaired before enabling OptiScaler chaining.");
            if (finalReShade)
                ProtectCoexistenceTarget(manifest, game, coexist, managedCoexist);
            var movedReShade = false;
            var repairedReShadeBeforeMove = false;
            if (!managedOptiProxy && (managedReShadeProxy || changedManagedReShadeProxy))
            {
                if (changedManagedReShadeProxy)
                {
                    AddManagedUpdate(plan, manifest, game, artifacts.ReShade!, proxy);
                    MarkRepair(plan);
                    repairedReShadeBeforeMove = true;
                }
                plan.Operations.Add(new(DeploymentOperationType.Move, coexist, proxy, Component: ComponentKind.ReShade,
                    Description: "Move managed ReShade behind OptiScaler"));
                AddMoveDecision(plan, ComponentKind.ReShade, proxy, coexist,
                    "Managed ReShade must move behind the selected OptiScaler proxy.");
                managedCoexist = true;
                movedReShade = true;
            }

            if (artifacts.ReShade is not null)
            {
                if (!repairedReShadeBeforeMove)
                {
                    if (movedReShade)
                        AddReplacementAfterPlannedMove(plan, game, artifacts.ReShade, coexist);
                    else
                        await AddReplacementAsync(plan, manifest, game, artifacts.ReShade, coexist,
                            officialArtifacts, false, cancellationToken);
                }
            }

            if (artifacts.OptiScaler is not null)
                await AddReplacementAsync(plan, manifest, game, artifacts.OptiScaler, proxy,
                    officialArtifacts, false, cancellationToken);

            await AddOptiScalerSupportFilesAsync(plan, manifest, game, selectedSupportFiles,
                officialArtifacts, cancellationToken);
            AddManagedOptiScalerMigrations(plan, manifest, game,
                artifacts with { OptiScalerSupportFiles = selectedSupportFiles });
            ConfigureOptiScaler(plan, manifest, game,
                artifacts.OptiScaler?.Version ?? VersionOf(manifest, ComponentKind.OptiScaler),
                TemplatePath(selectedSupportFiles), finalReShade, finalReShade);
        }
        else if (artifacts.ReShade is not null)
        {
            var repairsStandaloneLayout = AddStandaloneReShadeRepair(plan, manifest, game, coexist);
            var preservesUnownedCoexist = File.Exists(coexist) && !IsOwned(manifest, game, coexist) &&
                !repairsStandaloneLayout;
            if (preservesUnownedCoexist)
                plan.Warnings.Add("An unrelated ReShade64.dll was left untouched; standalone ReShade will use the selected active proxy.");
            await AddReplacementAsync(plan, manifest, game, artifacts.ReShade, proxy,
                officialArtifacts, false, cancellationToken);
            if (!preservesUnownedCoexist)
                plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, coexist, Value: "absent",
                    Description: "Verify standalone ReShade is not left in the coexistence slot"));
        }

        if (artifacts.RenoDx is not null)
        {
            if (!finalReShade)
                throw new InvalidOperationException("RenoDX requires full-addon ReShade in the same transaction.");
            var addonDirectory = ResolveAddonDirectory(game.DeploymentDirectory, game.GameRoot);
            plan.Operations.Add(new(DeploymentOperationType.CreateDirectory, addonDirectory));
            await AddReplacementAsync(plan, manifest, game, artifacts.RenoDx,
                Path.Combine(addonDirectory, artifacts.RenoDx.FileName), officialArtifacts, false, cancellationToken);
        }

        AddFinalLayoutVerification(plan, game, artifacts, proxy, coexist, finalOpti, finalReShade);
        if (finalReShade)
            ReShadePresetService.ConfigureFreshInstallation(plan, manifest, game);
        plan.LaunchOption = finalOpti || finalReShade ? GenerateLaunchOption(proxyName) : null;
        SetExpectation(plan, ComponentKind.ReShade, finalReShade);
        SetExpectation(plan, ComponentKind.RenoDx,
            artifacts.RenoDx is not null || HasValidManagedFile(manifest, game, ComponentKind.RenoDx));
        SetExpectation(plan, ComponentKind.OptiScaler, finalOpti);
        SetExpectation(plan, ComponentKind.OptiPatcher, false);
        AddManifest(plan, game);
        return plan;
    }

    public async Task<DeploymentPlan> BuildInstallPlanAsync(
        SteamGame game,
        ComponentArtifact artifact,
        string proxyName = "dxgi.dll",
        CancellationToken cancellationToken = default)
    {
        ValidateArtifact(game, artifact);
        await ValidateDigestAsync(artifact, cancellationToken);
        ValidateProxy(proxyName);
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
        EnsureManifestBelongsToGame(manifest, game);
        var plan = NewPlan(game, $"install {artifact.Component}");
        if (artifact.Component is ComponentKind.ReShade or ComponentKind.OptiScaler)
        {
            var diagnosis = await new ProxyDiagnosticsService().DiagnoseAsync(
                game, artifact.Component, [artifact], cancellationToken);
            var requested = diagnosis.Candidates.FirstOrDefault(x =>
                x.ProxyName.Equals(proxyName, StringComparison.OrdinalIgnoreCase));
            if (requested is null || !requested.SafeForNewInstallation || requested.ProhibitedByProfile)
            {
                proxyName = diagnosis.SelectedProxy ?? throw BuildProxyConflict(diagnosis, game, artifact.Component);
                plan.CompatibilityMessage = "A safe compatibility filename is available without changing existing game files.";
            }
        }
        if (game.RequiresConfirmation)
            plan.Warnings.Add("Anti-cheat files were detected. Explicit compatibility confirmation is required.");
        plan.Operations.Add(new(DeploymentOperationType.CreateDirectory, game.DeploymentDirectory));
        var existingOptiProxy = manifest.Files.FirstOrDefault(file =>
            file.Component == ComponentKind.OptiScaler &&
            SupportedProxyNames.Contains(Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase));
        if (existingOptiProxy is not null &&
            artifact.Component is ComponentKind.OptiScaler or ComponentKind.RenoDx)
            proxyName = Path.GetFileName(existingOptiProxy.RelativePath);
        var proxy = Path.Combine(game.DeploymentDirectory, proxyName);
        var coexist = Path.Combine(game.DeploymentDirectory, "ReShade64.dll");
        var optiPresent = existingOptiProxy is not null &&
            File.Exists(Path.Combine(game.GameRoot, existingOptiProxy.RelativePath));

        switch (artifact.Component)
        {
            case ComponentKind.ReShade:
                if (optiPresent)
                {
                    ProtectCoexistenceTarget(manifest, game, coexist,
                        ManagedAt(manifest, game, coexist, ComponentKind.ReShade));
                    await AddReplacementAsync(plan, manifest, game, artifact, coexist, [artifact], false, cancellationToken);
                    ConfigureOptiScaler(plan, manifest, game,
                        VersionOf(manifest, ComponentKind.OptiScaler), null, true, true);
                }
                else
                {
                    _ = AddStandaloneReShadeRepair(plan, manifest, game, coexist);
                    await AddReplacementAsync(plan, manifest, game, artifact, proxy, [artifact], false, cancellationToken);
                    plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, coexist, Value: "absent"));
                }
                break;
            case ComponentKind.RenoDx:
                var validReShadeHost = manifest.Files.Where(file => file.Component == ComponentKind.ReShade)
                    .Any(file =>
                    {
                        var path = Path.Combine(game.GameRoot, file.RelativePath);
                        if (!File.Exists(path) ||
                            !HashPath(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                        var name = Path.GetFileName(path);
                        return SupportedProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                            optiPresent && name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase);
                    });
                if (!validReShadeHost)
                    throw new InvalidOperationException(
                        "RenoDX needs full-addon ReShade in the same install. Use the recommended setup so both are installed together.");
                var addonDirectory = ResolveAddonDirectory(game.DeploymentDirectory, game.GameRoot);
                plan.Operations.Add(new(DeploymentOperationType.CreateDirectory, addonDirectory));
                await AddReplacementAsync(plan, manifest, game, artifact,
                    Path.Combine(addonDirectory, artifact.FileName), [artifact], false, cancellationToken);
                if (optiPresent)
                    ConfigureOptiScaler(plan, manifest, game,
                        VersionOf(manifest, ComponentKind.OptiScaler), null, true, true);
                break;
            case ComponentKind.OptiScaler:
                var reshadePresent = ManagedAt(manifest, game, proxy, ComponentKind.ReShade) && File.Exists(proxy);
                var coexistPresent = ManagedAt(manifest, game, coexist, ComponentKind.ReShade) && File.Exists(coexist);
                ProtectCoexistenceTarget(manifest, game, coexist, coexistPresent);
                if (reshadePresent)
                    plan.Operations.Add(new(DeploymentOperationType.Move, coexist, proxy, Component: ComponentKind.ReShade,
                        Description: "Move managed ReShade behind OptiScaler"));
                await AddReplacementAsync(plan, manifest, game, artifact, proxy, [artifact], false, cancellationToken);
                ConfigureOptiScaler(plan, manifest, game, artifact.Version, null,
                    reshadePresent || coexistPresent, reshadePresent || coexistPresent);
                if (reshadePresent || coexistPresent)
                {
                    plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, proxy, Value: "exists",
                        Description: "Verify OptiScaler owns the active proxy after migration"));
                    plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, coexist, Value: "exists",
                        Description: "Verify ReShade was migrated to ReShade64.dll"));
                    plan.Operations.Add(new(DeploymentOperationType.VerifyIniValue,
                        Path.Combine(game.DeploymentDirectory, "OptiScaler.ini"),
                        Value: "Plugins:LoadReshade=true",
                        Component: ComponentKind.OptiScaler,
                        Description: "Verify OptiScaler chains ReShade"));
                    plan.Operations.Add(new(DeploymentOperationType.VerifyIniValue,
                        Path.Combine(game.DeploymentDirectory, "OptiScaler.ini"),
                        Value: "Plugins:LoadAsiPlugins=true",
                        Component: ComponentKind.OptiScaler,
                        Description: "Verify OptiScaler loads ASI plugins"));
                    if (HasValidManagedFile(manifest, game, ComponentKind.RenoDx))
                    {
                        var reno = manifest.Files.First(file =>
                            file.Component == ComponentKind.RenoDx &&
                            File.Exists(Path.Combine(game.GameRoot, file.RelativePath)));
                        plan.Operations.Add(new(DeploymentOperationType.VerifyFileState,
                            Path.Combine(game.GameRoot, reno.RelativePath), Value: "exists",
                            Description: "Verify RenoDX addon remains hosted by ReShade"));
                    }
                }
                break;
            case ComponentKind.OptiPatcher:
                throw new NotSupportedException("OptiPatcher is legacy-cleanup-only and cannot be installed.");
        }

        plan.Operations.Add(new(DeploymentOperationType.VerifySha256,
            artifact.Component == ComponentKind.ReShade && optiPresent ? coexist : TargetFor(game, artifact, proxy),
            ExpectedSha256: NormalizedHash(artifact), Description: $"Verify installed {artifact.Component}"));
        if (artifact.Component == ComponentKind.ReShade ||
            artifact.Component == ComponentKind.RenoDx && HasValidManagedFile(manifest, game, ComponentKind.ReShade) ||
            artifact.Component == ComponentKind.OptiScaler && HasValidManagedFile(manifest, game, ComponentKind.ReShade))
            ReShadePresetService.ConfigureFreshInstallation(plan, manifest, game);
        plan.LaunchOption = artifact.Component is ComponentKind.ReShade or ComponentKind.OptiScaler
            ? GenerateLaunchOption(proxyName) : null;
        SetExpectation(plan, artifact.Component, true);
        if (artifact.Component == ComponentKind.OptiScaler)
        {
            var finalReShade = ManagedAt(manifest, game, proxy, ComponentKind.ReShade) && File.Exists(proxy) ||
                ManagedAt(manifest, game, coexist, ComponentKind.ReShade) && File.Exists(coexist) ||
                HasValidManagedFile(manifest, game, ComponentKind.ReShade);
            if (finalReShade) SetExpectation(plan, ComponentKind.ReShade, true);
            if (HasValidManagedFile(manifest, game, ComponentKind.RenoDx))
                SetExpectation(plan, ComponentKind.RenoDx, true);
        }
        AddManifest(plan, game);
        return plan;
    }

    public async Task<DeploymentPlan> BuildRemovePlanAsync(
        SteamGame game,
        ComponentKind component,
        string proxyName = "dxgi.dll",
        CancellationToken cancellationToken = default)
    {
        ValidateProxy(proxyName);
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
        EnsureManifestBelongsToGame(manifest, game);
        var plan = NewPlan(game, $"remove {component}");
        var optiProxyRecord = manifest.Files.FirstOrDefault(x => x.Component == ComponentKind.OptiScaler &&
            SupportedProxyNames.Contains(Path.GetFileName(x.RelativePath), StringComparer.OrdinalIgnoreCase));
        var actualProxyName = optiProxyRecord is null ? proxyName : Path.GetFileName(optiProxyRecord.RelativePath);
        var proxy = Path.Combine(game.DeploymentDirectory, actualProxyName);
        var coexist = Path.Combine(game.DeploymentDirectory, "ReShade64.dll");
        var optiRemains = component != ComponentKind.OptiScaler && optiProxyRecord is not null;

        var componentsToRemove = component switch
        {
            ComponentKind.OptiScaler => new[] { ComponentKind.OptiScaler, ComponentKind.OptiPatcher },
            ComponentKind.ReShade when manifest.Files.Any(x => x.Component == ComponentKind.RenoDx) =>
                new[] { ComponentKind.ReShade, ComponentKind.RenoDx },
            _ => new[] { component }
        };

        if (component == ComponentKind.ReShade &&
            componentsToRemove.Contains(ComponentKind.RenoDx))
        {
            plan.Warnings.Add(
                "RenoDX requires ReShade. Removing ReShade will also remove the managed RenoDX addon. Confirm to continue, or remove RenoDX first if you want a different order.");
        }

        foreach (var file in manifest.Files.Where(x => componentsToRemove.Contains(x.Component)).ToArray())
        {
            var ownership = ClassifyRemovalOwnership(file, component, componentsToRemove, manifest);
            if (ownership is RemovalPathOwnership.ManagedBySelectedComponent ||
                (ownership == RemovalPathOwnership.SharedManagedDependency &&
                 !RemainingComponentRequires(file, componentsToRemove, manifest)))
                AddManagedRemoval(plan, manifest, game, file, ownership);
            else
            {
                plan.FileDecisions.Add(new(file.Component, DeploymentFileRequirement.NeverReplace,
                    DeploymentFileAction.PreserveExisting,
                    $"Preserved as {ownership}; removal is limited to files proven to belong to the selected component.",
                    file.Version, file.RelativePath, Path.Combine(game.GameRoot, file.RelativePath),
                    false, true, true, []));
            }
        }

        if (component == ComponentKind.OptiScaler)
            plan.Operations.Add(new(DeploymentOperationType.ClearConfigurationPatches,
                Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json"), Component: ComponentKind.OptiScaler));

        if (component == ComponentKind.OptiScaler)
        {
            var managedCoexist = ManagedAt(manifest, game, coexist, ComponentKind.ReShade);
            if (managedCoexist && File.Exists(coexist))
            {
                plan.Operations.Add(new(DeploymentOperationType.Move, proxy, coexist, Component: ComponentKind.ReShade,
                    Description: $"Restore direct ReShade loading as {actualProxyName}"));
                plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, proxy, Value: "exists"));
                plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, coexist, Value: "absent"));
            }
        }
        else if ((component is ComponentKind.ReShade or ComponentKind.RenoDx) && optiRemains)
        {
            var keepReShadeChain = component == ComponentKind.RenoDx &&
                manifest.Files.Any(x => x.Component == ComponentKind.ReShade);
            ConfigureOptiScaler(plan, manifest, game, VersionOf(manifest, ComponentKind.OptiScaler), null,
                keepReShadeChain, keepReShadeChain, restorePreviousConfiguration: !keepReShadeChain);
            plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, proxy, Value: "exists"));
        }

        foreach (var removedComponent in componentsToRemove)
        {
            plan.Operations.Add(new(DeploymentOperationType.VerifyComponentRemoved, game.DeploymentDirectory,
                Component: removedComponent, Description: $"Rescan ownership state and verify {removedComponent} was removed"));
            SetExpectation(plan, removedComponent, false);
        }
        if (component == ComponentKind.OptiScaler && HasValidManagedFile(manifest, game, ComponentKind.ReShade))
            SetExpectation(plan, ComponentKind.ReShade, true);
        if (component is ComponentKind.ReShade or ComponentKind.RenoDx && optiRemains)
            SetExpectation(plan, ComponentKind.OptiScaler, true);

        var remainingProxy = RemainingManagedProxyName(manifest, game, componentsToRemove);
        plan.LaunchOption = remainingProxy is null ? null : GenerateLaunchOption(remainingProxy);
        AddManifest(plan, game);
        return plan;
    }

    public async Task<DeploymentPlan> BuildRemoveStackPlanAsync(
        SteamGame game,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
        EnsureManifestBelongsToGame(manifest, game);
        var plan = NewPlan(game, "remove recommended setup");
        foreach (var file in manifest.Files.ToArray())
            AddManagedRemoval(plan, manifest, game, file);
        plan.Operations.Add(new(DeploymentOperationType.ClearConfigurationPatches,
            Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json"), Component: ComponentKind.OptiScaler));
        foreach (var component in Enum.GetValues<ComponentKind>())
        {
            plan.Operations.Add(new(DeploymentOperationType.VerifyComponentRemoved, game.DeploymentDirectory,
                Component: component, Description: $"Verify {component} was removed"));
            SetExpectation(plan, component, false);
        }
        plan.LaunchOption = null;
        AddManifest(plan, game);
        return plan;
    }

    public async Task<DeploymentPlan> BuildRepairPlanAsync(
        SteamGame game,
        ComponentKind component,
        ComponentArtifact artifact,
        string proxyName = "dxgi.dll",
        CancellationToken cancellationToken = default)
    {
        var snapshot = await new ComponentDetector().DetectStackAsync(game, cancellationToken: cancellationToken);
        var report = snapshot.ReportFor(component);
        var healthyStates = report is not null &&
            report.State is (ComponentLifecycleState.InstalledHealthy or
                ComponentLifecycleState.InstalledWithWarnings or
                ComponentLifecycleState.InstalledMetadataIncomplete or
                ComponentLifecycleState.InstalledUnmanaged or
                ComponentLifecycleState.UpdateAvailable);
        if (healthyStates &&
            report!.Evidence.RepairReason is null &&
            (snapshot.ConcreteDefects is null || snapshot.ConcreteDefects.Count == 0) &&
            !ManagedReShadePresetMissing(game, snapshot))
        {
            var healthy = NewPlan(game, $"repair {component}");
            healthy.RequiresRepair = false;
            healthy.CompatibilityMessage = "No repair needed. The OptiScaler installation is healthy.";
            if (component != ComponentKind.OptiScaler)
                healthy.CompatibilityMessage = "No repair needed. The installed stack is healthy.";
            healthy.Warnings.Add(healthy.CompatibilityMessage);
            SetExpectation(healthy, component, true);
            if (snapshot.ReportFor(ComponentKind.ReShade)?.State is ComponentLifecycleState.InstalledHealthy or
                ComponentLifecycleState.InstalledWithWarnings or ComponentLifecycleState.InstalledMetadataIncomplete or
                ComponentLifecycleState.UpdateAvailable)
                SetExpectation(healthy, ComponentKind.ReShade, true);
            if (snapshot.ReportFor(ComponentKind.RenoDx)?.State is ComponentLifecycleState.InstalledHealthy or
                ComponentLifecycleState.InstalledWithWarnings or ComponentLifecycleState.InstalledMetadataIncomplete or
                ComponentLifecycleState.UpdateAvailable)
                SetExpectation(healthy, ComponentKind.RenoDx, true);
            if (snapshot.ReportFor(ComponentKind.OptiScaler)?.State is ComponentLifecycleState.InstalledHealthy or
                ComponentLifecycleState.InstalledWithWarnings or ComponentLifecycleState.InstalledMetadataIncomplete or
                ComponentLifecycleState.UpdateAvailable)
                SetExpectation(healthy, ComponentKind.OptiScaler, true);
            healthy.LaunchOption = snapshot.LaunchOptionRequirement;
            return healthy;
        }

        var repairProxy = snapshot.ActiveProxy
            ?? snapshot.ReportFor(ComponentKind.OptiScaler)?.Evidence.ActiveProxy
            ?? snapshot.ReportFor(ComponentKind.OptiScaler)?.Evidence.MissingRequiredFiles
                .FirstOrDefault(name => SupportedProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            ?? proxyName;
        var plan = await BuildInstallPlanAsync(game, artifact, repairProxy, cancellationToken);
        plan.Action = $"repair {component}";
        plan.RequiresRepair = true;
        plan.CompatibilityMessage = "Some files from an earlier installation need to be repaired before continuing.";
        return plan;
    }

    public async Task<DeploymentPlan> BuildRestorePlanAsync(
        SteamGame game,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
        EnsureManifestBelongsToGame(manifest, game);
        var plan = NewPlan(game, "restore backups");
        foreach (var file in manifest.Files.Where(x => x.BackupRelativePath is not null))
        {
            if (!TryGetTrustedBackup(game, file, out var backup, out var backupError))
            {
                if (backupError is not null) plan.Warnings.Add(backupError);
                continue;
            }
            var target = ResolveGamePath(game, file.RelativePath);
            if (!File.Exists(target))
            {
                plan.Operations.Add(new(DeploymentOperationType.RestoreBackup, target, backup!,
                    ExpectedSha256: file.BackupSha256, Component: file.Component,
                    Description: $"Restore the ownership-recorded original {file.RelativePath}"));
                plan.FileDecisions.Add(new(file.Component, DeploymentFileRequirement.ObsoleteManaged,
                    DeploymentFileAction.MoveManaged,
                    "The current managed file is missing and its ownership record identifies this exact original backup.",
                    file.Version, file.BackupRelativePath, target, false, false, false, []));
            }
            else plan.Warnings.Add($"Backup not restored because target is occupied: {target}");
        }
        AddManifest(plan, game);
        return plan;
    }

    public static string GenerateLaunchOption(string proxyName)
    {
        ValidateProxy(proxyName);
        return $"WINEDLLOVERRIDES=\"{Path.GetFileNameWithoutExtension(proxyName)}=n,b\" %command%";
    }

    private static void ConfigureOptiScaler(
        DeploymentPlan plan,
        GameManifest manifest,
        SteamGame game,
        string? version,
        string? templatePath,
        bool enableReShade,
        bool enableAsi,
        bool restorePreviousConfiguration = false)
    {
        var ini = Path.Combine(game.DeploymentDirectory, "OptiScaler.ini");
        if (!File.Exists(ini) && templatePath is not null &&
            !plan.Operations.Any(x => x.Type == DeploymentOperationType.Copy &&
                                      x.Target.Equals(ini, StringComparison.Ordinal)))
        {
            AddCopy(plan, new(ComponentKind.OptiScaler, templatePath, "OptiScaler.ini", version), ini);
        }

        var restoredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (restorePreviousConfiguration && (!enableReShade || !enableAsi))
        {
            var relativeIni = Path.GetRelativePath(game.GameRoot, ini);
            var patches = manifest.ConfigurationPatches.Where(x =>
                x.RelativePath.Equals(relativeIni, StringComparison.Ordinal) &&
                (!enableReShade && (x.Key.Equals("LoadReshade", StringComparison.OrdinalIgnoreCase) ||
                                    x.Key.Contains("ReShade", StringComparison.OrdinalIgnoreCase)) ||
                 !enableAsi && x.Key.Equals("LoadAsiPlugins", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (patches.Length > 0)
            {
                foreach (var patch in patches)
                {
                    var restoredValue = patch.PreviousValue ?? "<remove>";
                    plan.Operations.Add(new(DeploymentOperationType.WriteIniValue, ini,
                        Value: $"{patch.Section}:{patch.Key}={restoredValue}", Component: ComponentKind.OptiScaler,
                        Description: $"Restore the previous {patch.Key} setting", ConfigurationSchema: patch.SchemaVersion,
                        ConfigurationVersion: patch.ReleaseVersion, ClearConfigurationPatch: true));
                    plan.Operations.Add(new(DeploymentOperationType.VerifyIniValue, ini,
                        Value: $"{patch.Section}:{patch.Key}={restoredValue}", Component: ComponentKind.OptiScaler,
                        Description: $"Verify restored {patch.Key} setting"));
                    restoredKeys.Add(patch.Key);
                }
                plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, ini, Value: "exists",
                    Description: "Previous managed compatibility settings restored"));
            }
        }

        if (restorePreviousConfiguration && !enableReShade && !enableAsi &&
            restoredKeys.Contains("LoadReshade") && restoredKeys.Contains("LoadAsiPlugins"))
            return;

        var unownedExistingIni = File.Exists(ini) && !IsOwned(manifest, game, ini);
        IniDocument? existingDocument = null;
        if (File.Exists(ini))
        {
            var rawBytes = File.ReadAllBytes(ini);
            existingDocument = IniDocument.Parse(rawBytes);
            if (existingDocument.HasFatalIssues)
            {
                throw new DeploymentConflictException(
                    $"Required write: OptiScaler must update {ini}. The existing INI cannot be safely edited because it contains fatal corruption.");
            }
        }
        var schema = OptiScalerConfigurationAdapter.Detect(version, ini, templatePath, enableReShade, enableAsi);
        if (existingDocument is not null)
        {
            var managedKeys = schema.Changes.Select(change => (change.Section, change.Key)).ToArray();
            if (!existingDocument.CanSafelyEditManagedKeys(managedKeys))
            {
                if (templatePath is not null && File.Exists(templatePath) && IsOwned(manifest, game, ini))
                {
                    var quarantine = Path.Combine(game.GameRoot, ".rhi-linux", "backups", plan.Id,
                        Path.GetRelativePath(game.GameRoot, ini) + ".malformed");
                    plan.Warnings.Add(
                        "OptiScaler.ini contained malformed data that could not be patched safely. " +
                        "The original bytes will be preserved as a backup and a managed configuration will be written.");
                    if (!plan.Operations.Any(operation =>
                            operation.Type == DeploymentOperationType.Backup &&
                            operation.Target.Equals(ini, StringComparison.Ordinal)))
                    {
                        plan.Operations.Add(new(DeploymentOperationType.Backup, ini, BackupPath: quarantine,
                            Component: ComponentKind.OptiScaler,
                            Description: "Preserve the original malformed OptiScaler.ini bytes"));
                    }
                    if (!plan.Operations.Any(operation =>
                            operation.Type == DeploymentOperationType.Copy &&
                            operation.Target.Equals(ini, StringComparison.Ordinal)))
                    {
                        AddCopy(plan, new(ComponentKind.OptiScaler, templatePath, "OptiScaler.ini", version), ini);
                    }
                    existingDocument = null;
                }
                else
                {
                    var issue = existingDocument.Issues.FirstOrDefault()?.Message ??
                        "The INI contains a malformed section header.";
                    throw new DeploymentConflictException(
                        $"Required write: OptiScaler must update {ini}. The existing unowned INI is malformed and cannot be " +
                        $"safely edited or replaced. Alternative tested: preserve and patch in place; rejected because {issue}");
                }
            }
            else if (existingDocument.HasRecoverableIssues)
            {
                plan.Warnings.Add(
                    "OptiScaler.ini contains recoverable formatting issues. Managed keys will be updated while preserving unrelated lines.");
            }
        }
        string? backup = null;
        var alreadyBackedUp = plan.Operations.Any(operation =>
            operation.Type == DeploymentOperationType.Backup &&
            operation.Target.Equals(ini, StringComparison.Ordinal));
        if (!alreadyBackedUp && (unownedExistingIni || existingDocument?.HasRecoverableIssues == true))
        {
            backup = Path.Combine(game.GameRoot, ".rhi-linux", "backups", plan.Id, Path.GetRelativePath(game.GameRoot, ini));
            if (unownedExistingIni)
                plan.Warnings.Add("Existing OptiScaler settings will be preserved; only compatibility values will change.");
            if (!plan.FileDecisions.Any(decision =>
                    decision.DestinationPath.Equals(ini, StringComparison.Ordinal)))
                plan.FileDecisions.Add(new(ComponentKind.OptiScaler, DeploymentFileRequirement.Required,
                    DeploymentFileAction.PreserveExisting,
                    $"Use the validated OptiScaler {version ?? "template-detected"} schema, preserve unrelated user settings, " +
                    "and back up the original INI before applying the required compatibility keys.",
                    version, templatePath is null ? null : Path.GetFileName(templatePath), ini,
                    false, true, false, []));
        }

        foreach (var change in schema.Changes)
        {
            if (restoredKeys.Contains(change.Key)) continue;
            var diagnostic = $"OptiScaler {schema.Version}; schema {schema.Name}; {change.Section}.{change.Key}: " +
                $"{change.PreviousValue ?? "<missing>"} -> {change.ResultingValue}";
            plan.Operations.Add(new(DeploymentOperationType.WriteIniValue, ini,
                Value: $"{change.Section}:{change.Key}={change.ResultingValue}", Component: ComponentKind.OptiScaler,
                Description: diagnostic, BackupPath: backup, ConfigurationSchema: schema.Name,
                ConfigurationVersion: schema.Version));
            plan.Operations.Add(new(DeploymentOperationType.VerifyIniValue, ini,
                Value: $"{change.Section}:{change.Key}={change.ResultingValue}", Component: ComponentKind.OptiScaler,
                Description: $"Verify {change.Key} setting"));
        }
        plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, ini, Value: "exists",
            Description: "Compatibility configured"));
    }

    private async Task AddOptiScalerSupportFilesAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        SteamGame game,
        IReadOnlyList<ComponentArtifact>? supportFiles,
        IReadOnlyList<ComponentArtifact> officialArtifacts,
        CancellationToken cancellationToken)
    {
        foreach (var support in supportFiles ?? [])
        {
            var extension = Path.GetExtension(support.FileName);
            var relative = support.RelativePath ?? support.FileName;
            var normalized = relative.Replace('\\', '/');
            if (normalized.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(relative) || normalized.Split('/').Contains("..", StringComparer.Ordinal) ||
                !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(game.DeploymentDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) && normalized.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) &&
                !IsOwned(manifest, game, target))
            {
                plan.Warnings.Add("Existing OptiScaler settings will be preserved; only required compatibility values will change.");
                continue;
            }
            await AddReplacementAsync(plan, manifest, game, support, target, officialArtifacts,
                allowPreserveGameOwned: true, cancellationToken: cancellationToken);
        }
    }

    private static void AddFinalLayoutVerification(
        DeploymentPlan plan,
        SteamGame game,
        RecommendedStackArtifacts artifacts,
        string proxy,
        string coexist,
        bool finalOpti,
        bool finalReShade)
    {
        var iniTargets = plan.Operations.Where(x => x.Type == DeploymentOperationType.WriteIniValue)
            .Select(x => x.Target).ToHashSet(StringComparer.Ordinal);
        foreach (var copy in plan.Operations.Where(x => x.Type == DeploymentOperationType.Copy &&
                     !iniTargets.Contains(x.Target)).GroupBy(x => x.Target, StringComparer.Ordinal).Select(x => x.Last()).ToArray())
            plan.Operations.Add(new(DeploymentOperationType.VerifySha256, copy.Target,
                ExpectedSha256: copy.ExpectedSha256, Description: $"Verify deployed {Path.GetFileName(copy.Target)}"));
        if (finalOpti)
            plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, proxy, Value: "exists",
                Description: "Verify managed OptiScaler owns the active proxy"));
        if (finalReShade)
            plan.Operations.Add(new(DeploymentOperationType.VerifyFileState, finalOpti ? coexist : proxy, Value: "exists",
                Description: finalOpti ? "Verify managed ReShade coexistence DLL" : "Verify managed ReShade owns the active proxy"));
        if (artifacts.RenoDx is not null)
            plan.Operations.Add(new(DeploymentOperationType.VerifyFileState,
                Path.Combine(ResolveAddonDirectory(game.DeploymentDirectory, game.GameRoot), artifacts.RenoDx.FileName), Value: "exists",
                Description: "Verify RenoDX addon retains its upstream filename"));
        if (finalOpti)
            plan.Operations.Add(new(DeploymentOperationType.VerifyManagedLayout, Path.GetDirectoryName(proxy)!,
                Source: proxy, Value: GenerateLaunchOption(Path.GetFileName(proxy)), Component: ComponentKind.OptiScaler,
                Description: "Verify the complete managed OptiScaler layout and Proton launch option"));
    }

    private static void AddManagedOptiScalerMigrations(
        DeploymentPlan plan,
        GameManifest manifest,
        SteamGame game,
        RecommendedStackArtifacts artifacts)
    {
        if (artifacts.OptiScalerBundle is null || artifacts.OptiScaler is null) return;
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeProxy = plan.Operations.LastOrDefault(x => x.Type == DeploymentOperationType.Copy &&
            x.Component == ComponentKind.OptiScaler &&
            SupportedProxyNames.Contains(Path.GetFileName(x.Target), StringComparer.OrdinalIgnoreCase))?.Target;
        if (activeProxy is not null) expected.Add(Path.GetRelativePath(game.GameRoot, activeProxy));
        foreach (var support in artifacts.OptiScalerSupportFiles ?? [])
        {
            var relative = support.RelativePath ?? support.FileName;
            expected.Add(Path.GetRelativePath(game.GameRoot,
                Path.Combine(game.DeploymentDirectory, relative.Replace('/', Path.DirectorySeparatorChar))));
        }

        foreach (var file in manifest.Files.Where(x =>
                     x.Component == ComponentKind.OptiPatcher ||
                     x.Component == ComponentKind.OptiScaler && !expected.Contains(x.RelativePath) ||
                     artifacts.OptiScalerBundle.ObsoleteManagedPaths.Any(obsolete =>
                         Path.GetFileName(x.RelativePath).Equals(obsolete, StringComparison.OrdinalIgnoreCase) &&
                         x.Component is ComponentKind.OptiScaler or ComponentKind.OptiPatcher)).ToArray())
        {
            if (plan.Operations.Any(x => x.Type == DeploymentOperationType.DeleteManagedFile &&
                                        Path.GetRelativePath(game.GameRoot, x.Target).Equals(file.RelativePath, StringComparison.Ordinal)))
                continue;
            AddManagedRemoval(plan, manifest, game, file);
        }
    }

    private static bool HasUnparseableManagedOptiScalerIni(SteamGame game, GameManifest manifest)
    {
        var record = manifest.Files.FirstOrDefault(file =>
            file.Component == ComponentKind.OptiScaler &&
            Path.GetFileName(file.RelativePath).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        if (record is null) return false;
        var path = Path.Combine(game.GameRoot, record.RelativePath);
        if (!File.Exists(path)) return false;
        try
        {
            var document = IniDocument.Parse(File.ReadAllBytes(path));
            if (document.HasFatalIssues) return true;
            return !document.CanSafelyEditManagedKeys(
            [
                ("Plugins", "LoadReshade"),
                ("Plugins", "LoadAsiPlugins")
            ]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return true;
        }
    }

    private static bool IsImmutableRuntimeDrift(SteamGame game, ManagedFile file)
    {
        if (!IsImmutableRuntimeFile(file)) return false;
        var path = Path.Combine(game.GameRoot, file.RelativePath);
        if (!File.Exists(path)) return true;
        if (HashPath(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        return !StackDetector.IsRecognizedRuntime(path, file.Component);
    }

    private static bool IsImmutableRuntimeFile(ManagedFile file)
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

    private static string? RemainingManagedProxyName(
        GameManifest manifest,
        SteamGame game,
        IReadOnlyList<ComponentKind> removedComponents)
    {
        var remaining = manifest.Files
            .Where(file => !removedComponents.Contains(file.Component))
            .Where(file => SupportedProxyNames.Contains(Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase))
            .Where(file => File.Exists(Path.Combine(game.GameRoot, file.RelativePath)))
            .Select(file => Path.GetFileName(file.RelativePath))
            .FirstOrDefault();
        if (remaining is not null) return remaining;

        if (removedComponents.Contains(ComponentKind.OptiScaler))
        {
            var reshadeProxy = manifest.Files.FirstOrDefault(file =>
                file.Component == ComponentKind.ReShade &&
                (SupportedProxyNames.Contains(Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase) ||
                 Path.GetFileName(file.RelativePath).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase)));
            if (reshadeProxy is not null)
            {
                var optiProxy = manifest.Files.FirstOrDefault(file =>
                    file.Component == ComponentKind.OptiScaler &&
                    SupportedProxyNames.Contains(Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase));
                return optiProxy is null ? Path.GetFileName(reshadeProxy.RelativePath) : Path.GetFileName(optiProxy.RelativePath);
            }
        }

        return null;
    }

    private static bool AddStandaloneReShadeRepair(
        DeploymentPlan plan,
        GameManifest manifest,
        SteamGame game,
        string coexistPath)
    {
        if (!File.Exists(coexistPath)) return false;

        var coexistRelativePath = Path.GetRelativePath(game.GameRoot, coexistPath);
        var coexistHash = HashPath(coexistPath);
        var exactRecord = manifest.Files.FirstOrDefault(file =>
            file.Component == ComponentKind.ReShade &&
            file.RelativePath.Equals(coexistRelativePath, StringComparison.Ordinal));
        if (exactRecord is not null)
        {
            if (coexistHash.Equals(exactRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                plan.Operations.Add(new(DeploymentOperationType.DeleteManagedFile, coexistPath,
                    Component: ComponentKind.ReShade,
                    Description: "Remove managed ReShade from the standalone coexistence slot before repair"));
                plan.FileDecisions.Add(new(ComponentKind.ReShade, DeploymentFileRequirement.ObsoleteManaged,
                    DeploymentFileAction.RemoveManaged,
                    "Managed ReShade is stranded in the coexistence slot without OptiScaler and must return to an active proxy filename.",
                    exactRecord.Version, exactRecord.BundleRelativePath, coexistPath, false, false, true,
                    ["Deploy the selected official ReShade build under the active proxy filename"]));
            }
            else
            {
                var recovery = Path.Combine(game.GameRoot, ".rhi-linux", "recovery", plan.Id,
                    coexistRelativePath + ".changed");
                plan.Operations.Add(new(DeploymentOperationType.Backup, coexistPath, recovery,
                    Component: ComponentKind.ReShade,
                    Description: "Preserve changed ReShade64.dll before repairing standalone loading"));
                plan.Operations.Add(new(DeploymentOperationType.ForgetOwnership, coexistPath,
                    Component: ComponentKind.ReShade,
                    Description: "Clear stale ownership for the changed standalone ReShade layout"));
                plan.FileDecisions.Add(new(ComponentKind.ReShade, DeploymentFileRequirement.ObsoleteManaged,
                    DeploymentFileAction.PreserveExisting,
                    "The stranded managed ReShade file changed, so it is moved to recovery storage before the official standalone proxy is restored.",
                    exactRecord.Version, exactRecord.BundleRelativePath, coexistPath, false, true, true,
                    [$"Recover the preserved file from {Path.GetRelativePath(game.GameRoot, recovery)}"]));
                plan.Warnings.Add("The changed ReShade64.dll will be preserved in recovery storage before standalone ReShade is repaired.");
            }

            MarkRepair(plan);
            return true;
        }

        var staleRecords = manifest.Files.Where(file =>
                file.Component == ComponentKind.ReShade &&
                !File.Exists(ResolveGamePath(game, file.RelativePath)) &&
                coexistHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (staleRecords.Length == 0) return false;

        plan.Operations.Add(new(DeploymentOperationType.DeleteVerifiedFile, coexistPath,
            ExpectedSha256: coexistHash, Component: ComponentKind.ReShade,
            Description: "Remove the hash-verified stranded ReShade64.dll before restoring standalone loading"));
        foreach (var staleRecord in staleRecords)
        {
            plan.Operations.Add(new(DeploymentOperationType.ForgetOwnership,
                ResolveGamePath(game, staleRecord.RelativePath), Component: ComponentKind.ReShade,
                Description: $"Clear stale ReShade ownership for {staleRecord.RelativePath}"));
        }
        plan.FileDecisions.Add(new(ComponentKind.ReShade, DeploymentFileRequirement.ObsoleteManaged,
            DeploymentFileAction.RemoveManaged,
            "The file in the coexistence slot exactly matches missing managed ReShade history and is safely replaced by the selected standalone proxy.",
            staleRecords[0].Version, staleRecords[0].BundleRelativePath, coexistPath, false, false, true,
            ["Deploy the selected official ReShade build under the active proxy filename"]));
        MarkRepair(plan);
        return true;
    }

    private static void MarkRepair(DeploymentPlan plan)
    {
        plan.RequiresRepair = true;
        plan.CompatibilityMessage = "Some files from an earlier installation need to be repaired before continuing.";
    }

    private static void AddManagedRemoval(
        DeploymentPlan plan,
        GameManifest manifest,
        SteamGame game,
        ManagedFile file,
        RemovalPathOwnership ownership = RemovalPathOwnership.ManagedBySelectedComponent)
    {
        var target = Path.Combine(game.GameRoot, file.RelativePath);
        var hasTrustedBackup = TryGetTrustedBackup(game, file, out var trustedBackup, out var backupError);
        if (backupError is not null) plan.Warnings.Add(backupError);
        if (!File.Exists(target))
        {
            plan.Operations.Add(new(DeploymentOperationType.ForgetOwnership, target, Component: file.Component,
                Description: $"Clear stale ownership for missing {file.RelativePath}"));
            if (hasTrustedBackup)
                plan.Operations.Add(new(DeploymentOperationType.RestoreBackup, target, trustedBackup,
                    ExpectedSha256: file.BackupSha256, Component: file.Component,
                    Description: $"Restore original {file.RelativePath}"));
            plan.FileDecisions.Add(new(file.Component, DeploymentFileRequirement.ObsoleteManaged,
                DeploymentFileAction.RemoveManaged,
                $"Classified as {ownership}; ownership metadata for the missing file is cleared.",
                file.Version, file.BundleRelativePath, target, false, false, false, []));
            return;
        }
        if (!HashPath(target).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var recovery = Path.Combine(game.GameRoot, ".rhi-linux", "recovery", plan.Id,
                file.RelativePath + ".changed");
            plan.Operations.Add(new(DeploymentOperationType.Backup, target, recovery, Component: file.Component,
                Description: $"Preserve changed managed file in recovery storage: {file.RelativePath}"));
            plan.Operations.Add(new(DeploymentOperationType.ForgetOwnership, target, Component: file.Component,
                Description: $"Clear stale ownership after preserving changed file {file.RelativePath}"));
            if (hasTrustedBackup)
                plan.Operations.Add(new(DeploymentOperationType.RestoreBackup, target, trustedBackup,
                    ExpectedSha256: file.BackupSha256, Component: file.Component,
                    Description: $"Restore the hash-verified original {file.RelativePath} after preserving the changed managed copy"));
            plan.FileDecisions.Add(new(file.Component, DeploymentFileRequirement.ObsoleteManaged,
                DeploymentFileAction.PreserveExisting,
                hasTrustedBackup
                    ? $"Classified as {ownership}. The changed managed file is preserved in recovery storage, then the hash-verified original backup is restored."
                    : $"Classified as {ownership}. The file was previously managed but its current hash changed, so removal moves it to recovery storage instead of deleting it.",
                file.Version, file.BundleRelativePath, target, false, true, true,
                ["Keep the changed file in recovery storage"]));
            return;
        }

        plan.Operations.Add(new(DeploymentOperationType.DeleteManagedFile, target,
            ExpectedSha256: file.Sha256, Component: file.Component,
            Description: $"Remove managed {file.RelativePath} ({ownership})"));
        if (hasTrustedBackup)
            plan.Operations.Add(new(DeploymentOperationType.RestoreBackup, target, trustedBackup,
                ExpectedSha256: file.BackupSha256, Component: file.Component,
                Description: $"Restore original {file.RelativePath}"));
        plan.FileDecisions.Add(new(file.Component, DeploymentFileRequirement.ObsoleteManaged,
            DeploymentFileAction.RemoveManaged,
            $"Classified as {ownership}; delete only after the current hash matches the ownership record.",
            file.Version, file.BundleRelativePath, target, false, false, hasTrustedBackup,
            hasTrustedBackup ? ["Restore the original game-owned backup"] : []));
    }

    public static RemovalPathOwnership ClassifyRemovalOwnership(
        ManagedFile file,
        ComponentKind selectedComponent,
        IReadOnlyList<ComponentKind> componentsToRemove,
        GameManifest manifest)
    {
        if (file.Component == selectedComponent)
            return RemovalPathOwnership.ManagedBySelectedComponent;
        if (componentsToRemove.Contains(file.Component))
            return RemovalPathOwnership.SharedManagedDependency;
        if (manifest.Files.Any(x => x.Component == file.Component))
            return RemovalPathOwnership.ManagedByAnotherComponent;
        return RemovalPathOwnership.Unknown;
    }

    private static bool RemainingComponentRequires(
        ManagedFile file,
        IReadOnlyList<ComponentKind> componentsToRemove,
        GameManifest manifest)
    {
        foreach (var other in manifest.Files)
        {
            if (componentsToRemove.Contains(other.Component)) continue;
            if (other.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task AddReplacementAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        SteamGame game,
        ComponentArtifact artifact,
        string target,
        IReadOnlyList<ComponentArtifact> officialArtifacts,
        bool allowPreserveGameOwned,
        CancellationToken cancellationToken)
    {
        if (plan.Operations.Any(x => x.Type == DeploymentOperationType.Move &&
            x.Source is not null && Path.GetFullPath(x.Source).Equals(Path.GetFullPath(target), StringComparison.Ordinal)))
        {
            AddCopy(plan, artifact, target);
            return;
        }
        if (!File.Exists(target))
        {
            AddCopy(plan, artifact, target);
            return;
        }
        if (!Path.GetExtension(target).Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(target).StartsWith(".addon", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsOwned(manifest, game, target))
            {
                if (artifact.CanOmitOnCollision || artifact.Requirement is DeploymentFileRequirement.Optional or DeploymentFileRequirement.Conditional)
                {
                    var existingHash = HashPath(target);
                    plan.CompatibilityMessage ??= "A safe compatibility filename is available without changing existing game files.";
                    plan.Operations.Add(new(DeploymentOperationType.VerifySha256, target, ExpectedSha256: existingHash,
                        Description: $"Verify preserved existing file {Path.GetRelativePath(game.GameRoot, target)} was not changed"));
                    AddFileDecision(plan, artifact, target, DeploymentFileAction.PreserveExisting,
                        "The optional destination is occupied by an unowned file, so the bundled copy is omitted.",
                        false, true, true, [$"Install without {artifact.Feature ?? "this optional feature"}"]);
                    return;
                }
                throw new DeploymentConflictException($"Unknown target-side file: {Path.GetRelativePath(game.GameRoot, target)}");
            }
            AddManagedUpdate(plan, manifest, game, artifact, target);
            return;
        }

        var diagnostic = await targetFileClassifier.ClassifyAsync(
            game, manifest, target, officialArtifacts, cancellationToken);
        var expected = NormalizedHash(artifact);
        if (diagnostic.Sha256?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true)
        {
            plan.Operations.Add(new(DeploymentOperationType.TrackExistingFile, target, Value: artifact.Version,
                ExpectedSha256: expected, Component: artifact.Component,
                Description: $"Adopt byte-identical official {artifact.Component} file", SourceUrl: artifact.SourceUrl,
                SourceBlobSha256: artifact.SourceBlobSha256 ?? expected,
                SourceBundleSha256: artifact.SourceBundleSha256, BundleRelativePath: artifact.RelativePath));
            AddFileDecision(plan, artifact, target, DeploymentFileAction.AdoptOfficial,
                "The existing destination is byte-identical to the selected official release file.", false, false, false, []);
            return;
        }

        switch (diagnostic.Classification)
        {
            case TargetFileClassification.ManagedByRhiLinux:
                AddManagedUpdate(plan, manifest, game, artifact, target);
                return;
            case TargetFileClassification.ChangedManagedFile:
                plan.RequiresRepair = true;
                plan.CompatibilityMessage = "Some files from an earlier installation need to be repaired before continuing.";
                AddManagedUpdate(plan, manifest, game, artifact, target);
                return;
            case TargetFileClassification.RecoverableLeftover:
                plan.RequiresRepair = true;
                plan.CompatibilityMessage = "Some files from an earlier installation need to be repaired before continuing.";
                foreach (var stale in manifest.Files.Where(x =>
                             diagnostic.Sha256!.Equals(x.Sha256, StringComparison.OrdinalIgnoreCase) &&
                             !Path.GetFullPath(Path.Combine(game.GameRoot, x.RelativePath))
                                 .Equals(Path.GetFullPath(target), StringComparison.Ordinal)))
                    plan.Operations.Add(new(DeploymentOperationType.ForgetOwnership,
                        Path.Combine(game.GameRoot, stale.RelativePath), Component: stale.Component,
                        Description: $"Clear stale ownership for relocated {stale.RelativePath}"));
                plan.Operations.Add(new(DeploymentOperationType.DeleteVerifiedFile, target,
                    ExpectedSha256: diagnostic.Sha256, Component: artifact.Component,
                    Description: $"Clean up verified leftover {Path.GetRelativePath(game.GameRoot, target)}"));
                AddCopy(plan, artifact, target);
                return;
            case TargetFileClassification.GameOwned when allowPreserveGameOwned:
                plan.CompatibilityMessage ??= "A safe compatibility filename is available without changing existing game files.";
                plan.Operations.Add(new(DeploymentOperationType.VerifySha256, target,
                    ExpectedSha256: diagnostic.Sha256,
                    Description: $"Verify preserved game file {Path.GetRelativePath(game.GameRoot, target)} was not changed"));
                AddFileDecision(plan, artifact, target, DeploymentFileAction.PreserveExisting,
                    "The selected feature can use or coexist with the game-supplied file, so the bundled copy is omitted.",
                    false, true, true, ["Use the existing game file", $"Install without {artifact.Feature ?? "this optional feature"}"]);
                return;
            default:
                if (artifact.CanOmitOnCollision || artifact.Requirement is DeploymentFileRequirement.Optional or DeploymentFileRequirement.Conditional)
                {
                    plan.CompatibilityMessage ??= "A safe compatibility filename is available without changing existing game files.";
                    plan.Operations.Add(new(DeploymentOperationType.VerifySha256, target,
                        ExpectedSha256: diagnostic.Sha256,
                        Description: $"Verify preserved existing file {Path.GetRelativePath(game.GameRoot, target)} was not changed"));
                    AddFileDecision(plan, artifact, target, DeploymentFileAction.PreserveExisting,
                        $"The existing file is {diagnostic.Classification}; the optional bundled feature copy is omitted instead of replacing it.",
                        false, true, true, [$"Install without {artifact.Feature ?? "this optional feature"}"]);
                    return;
                }
                var relative = Path.GetRelativePath(game.GameRoot, target);
                throw new DeploymentConflictException(
                    $"{relative}: {TargetFileClassification.HardConflict} from {diagnostic.Classification}. " +
                    string.Join("; ", diagnostic.Evidence));
        }
    }

    private static void AddManagedUpdate(
        DeploymentPlan plan,
        GameManifest manifest,
        SteamGame game,
        ComponentArtifact artifact,
        string target)
    {
        if (File.Exists(target))
        {
            if (!IsOwned(manifest, game, target))
            {
                throw new DeploymentConflictException($"Unowned target-side file: {Path.GetRelativePath(game.GameRoot, target)}");
            }
            var rollbackBackup = Path.Combine(game.GameRoot, ".rhi-linux", "backups", plan.Id,
                Path.GetRelativePath(game.GameRoot, target));
            plan.Operations.Add(new(DeploymentOperationType.Backup, target, rollbackBackup, Component: artifact.Component));
        }
        AddCopy(plan, artifact, target, DeploymentFileAction.ReplaceManaged,
            "Replace the ownership-verified managed file with the selected official release file.");
    }

    private static void AddCopy(
        DeploymentPlan plan,
        ComponentArtifact artifact,
        string target,
        DeploymentFileAction action = DeploymentFileAction.Deploy,
        string? reason = null)
    {
        plan.Operations.Add(new(DeploymentOperationType.Copy, target, artifact.Path, artifact.Version,
            NormalizedHash(artifact), artifact.Component, BackupPath: null, SourceUrl: artifact.SourceUrl,
            SourceBlobSha256: artifact.SourceBlobSha256 ?? artifact.Sha256,
            SourceBundleSha256: artifact.SourceBundleSha256, BundleRelativePath: artifact.RelativePath,
            Purpose: artifact.Feature, Requirement: artifact.Requirement));
        AddFileDecision(plan, artifact, target, action,
            reason ?? artifact.RequirementReason ?? "The selected installation strategy requires this official file.",
            action == DeploymentFileAction.ReplaceManaged, false,
            artifact.CanOmitOnCollision, artifact.CanOmitOnCollision ? [$"Install without {artifact.Feature ?? "this optional feature"}"] : []);
    }

    private static void AddReplacementAfterPlannedMove(
        DeploymentPlan plan,
        SteamGame game,
        ComponentArtifact artifact,
        string target)
    {
        var rollbackBackup = Path.Combine(game.GameRoot, ".rhi-linux", "backups", plan.Id,
            Path.GetRelativePath(game.GameRoot, target));
        plan.Operations.Add(new(DeploymentOperationType.Backup, target, rollbackBackup, Component: artifact.Component,
            Description: "Back up the renamed managed ReShade before updating it"));
        plan.Operations.Add(new(DeploymentOperationType.Copy, target, artifact.Path, artifact.Version,
            NormalizedHash(artifact), artifact.Component, BackupPath: null, SourceUrl: artifact.SourceUrl,
            SourceBlobSha256: artifact.SourceBlobSha256 ?? artifact.Sha256,
            SourceBundleSha256: artifact.SourceBundleSha256, BundleRelativePath: artifact.RelativePath,
            Purpose: artifact.Feature, Requirement: artifact.Requirement));
        AddFileDecision(plan, artifact, target, DeploymentFileAction.ReplaceManaged,
            "Update the managed ReShade file after moving it into the coexistence slot.", true, false, false, []);
    }

    private static void ProtectCoexistenceTarget(GameManifest manifest, SteamGame game, string coexist, bool managedCoexist)
    {
        if (File.Exists(coexist) && !managedCoexist && !IsOwned(manifest, game, coexist))
            throw new DeploymentConflictException("ReShade64.dll is occupied by an unowned file; coexistence cannot be configured safely.");
    }

    private static string TargetFor(SteamGame game, ComponentArtifact artifact, string proxy) => artifact.Component switch
    {
        ComponentKind.ReShade or ComponentKind.OptiScaler => proxy,
        ComponentKind.RenoDx => Path.Combine(ResolveAddonDirectory(game.DeploymentDirectory, game.GameRoot), artifact.FileName),
        ComponentKind.OptiPatcher => throw new NotSupportedException("OptiPatcher is legacy-cleanup-only."),
        _ => throw new ArgumentOutOfRangeException(nameof(artifact))
    };

    private static string? TemplatePath(IReadOnlyList<ComponentArtifact>? supportFiles) => supportFiles?
        .FirstOrDefault(x => x.FileName.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))?.Path;

    private static IReadOnlyList<ComponentArtifact> SelectOptiScalerSupportFiles(
        SteamGame game,
        GameProfile profile,
        IReadOnlyList<ComponentArtifact> supportFiles)
    {
        var renderer = profile.Renderer;
        var enableAgilityUpgrade = profile.RequiredIniValues.Any(x =>
            x.Key.Equals("FsrAgilitySDKUpgrade", StringComparison.OrdinalIgnoreCase) &&
            x.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
        bool Applicable(ComponentArtifact artifact)
        {
            if (artifact.Requirement == DeploymentFileRequirement.Optional)
            {
                var relative = artifact.RelativePath ?? artifact.FileName;
                var destination = Path.Combine(game.DeploymentDirectory,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(destination) || artifact.Feature is not null &&
                    profile.RequiredIniValues.TryGetValue($"Feature:{artifact.Feature}", out var enabled) &&
                    enabled.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            return artifact.Feature switch
            {
                "fidelityfx-vulkan" => renderer.Contains("Vulkan", StringComparison.OrdinalIgnoreCase),
                "fidelityfx-dx12" => !renderer.Contains("DirectX 11", StringComparison.OrdinalIgnoreCase) &&
                    !renderer.Contains("Vulkan", StringComparison.OrdinalIgnoreCase),
                "xess-dx11" => renderer.Contains("DirectX 11", StringComparison.OrdinalIgnoreCase) ||
                    renderer.Equals("DirectX", StringComparison.OrdinalIgnoreCase),
                "d3d12-agility-upgrade" => enableAgilityUpgrade,
                _ => true
            };
        }
        return supportFiles.Where(x => x.Requirement == DeploymentFileRequirement.Required || Applicable(x)).ToArray();
    }

    private static void AddOmittedBundleDecisions(
        DeploymentPlan plan,
        SteamGame game,
        OptiScalerBundleManifest? bundle,
        IReadOnlyList<ComponentArtifact> selectedSupport)
    {
        if (bundle is null) return;
        var selected = selectedSupport.Select(x => x.RelativePath ?? x.FileName)
            .Append("OptiScaler.dll").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in bundle.Files.Where(x => !selected.Contains(x.RelativePath)))
        {
            var destination = Path.Combine(game.DeploymentDirectory,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            plan.FileDecisions.Add(new(ComponentKind.OptiScaler, file.Requirement, DeploymentFileAction.Omit,
                file.RequirementReason ?? "This archive entry is not required by the selected game strategy.",
                bundle.ReleaseVersion, file.RelativePath, destination, false, false, true,
                ["Keep the archive entry in the validated shared cache"]));
        }
    }

    private static void AddRejectedProxyDecisions(
        DeploymentPlan plan,
        ProxySelectionResult proxySelection,
        string selectedProxy,
        ComponentKind component)
    {
        foreach (var candidate in proxySelection.Candidates.Where(x =>
                     !x.ProxyName.Equals(selectedProxy, StringComparison.OrdinalIgnoreCase) && File.Exists(x.Path)))
        {
            plan.FileDecisions.Add(new(component, DeploymentFileRequirement.NeverReplace,
                DeploymentFileAction.PreserveExisting, candidate.Reason, null, null, candidate.Path,
                false, true, true, [$"Use {selectedProxy}"]));
        }
    }

    private static void AddFileDecision(
        DeploymentPlan plan,
        ComponentArtifact artifact,
        string target,
        DeploymentFileAction action,
        string reason,
        bool replacesManaged,
        bool preservesExisting,
        bool alternativeExists,
        IReadOnlyList<string> alternatives)
    {
        plan.FileDecisions.Add(new(artifact.Component, artifact.Requirement, action, reason,
            artifact.Version ?? artifact.SourceUrl, artifact.RelativePath ?? artifact.FileName, target,
            replacesManaged, preservesExisting, alternativeExists, alternatives));
    }

    private static void AddMoveDecision(
        DeploymentPlan plan,
        ComponentKind component,
        string source,
        string target,
        string reason)
    {
        plan.FileDecisions.Add(new(component, DeploymentFileRequirement.Required,
            DeploymentFileAction.MoveManaged, reason, null, Path.GetFileName(source), target,
            true, false, false, []));
    }

    private static void SetExpectation(DeploymentPlan plan, ComponentKind component, bool installed)
    {
        plan.ExpectedComponentStates.RemoveAll(x => x.Component == component);
        plan.ExpectedComponentStates.Add(new(component, installed));
    }

    private static bool HasValidManagedFile(GameManifest manifest, SteamGame game, ComponentKind component) =>
        manifest.Files.Where(x => x.Component == component).Any(x =>
        {
            var path = Path.Combine(game.GameRoot, x.RelativePath);
            return File.Exists(path) && HashPath(path).Equals(x.Sha256, StringComparison.OrdinalIgnoreCase);
        });

    private static void EnsureManifestBelongsToGame(GameManifest manifest, SteamGame game)
    {
        if (manifest.Files.Count > 0 && manifest.AppId != game.AppId)
            throw new DeploymentConflictException(
                $"The ownership manifest belongs to Steam AppID {manifest.AppId}, not selected AppID {game.AppId}. " +
                "No managed-file strategy can safely use another game's ownership records.");
        foreach (var file in manifest.Files)
        {
            _ = ResolveGamePath(game, file.RelativePath);
            if (file.BackupRelativePath is not null) _ = ResolveGamePath(game, file.BackupRelativePath);
        }
    }

    private static string ResolveGamePath(SteamGame game, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new DeploymentConflictException("The ownership manifest contains an absolute path outside its portable game-relative model.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.GameRoot));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.Equals(root, StringComparison.Ordinal) &&
            !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new DeploymentConflictException($"The ownership manifest path escapes the game directory: {relativePath}");
        return path;
    }

    private static string? VersionOf(GameManifest manifest, ComponentKind component) =>
        manifest.Files.FirstOrDefault(x => x.Component == component)?.Version;

    private static bool ManagedAt(GameManifest manifest, SteamGame game, string path, ComponentKind component) =>
        ManagedRecordAt(manifest, game, path, component) is not null;

    private static ManagedFile? ManagedRecordAt(
        GameManifest manifest,
        SteamGame game,
        string path,
        ComponentKind component) =>
        manifest.Files.FirstOrDefault(x => x.Component == component &&
            Path.GetFullPath(Path.Combine(game.GameRoot, x.RelativePath)).Equals(Path.GetFullPath(path), StringComparison.Ordinal));

    private static bool IsOwned(GameManifest manifest, SteamGame game, string path) =>
        manifest.Files.Any(x => Path.GetFullPath(Path.Combine(game.GameRoot, x.RelativePath))
            .Equals(Path.GetFullPath(path), StringComparison.Ordinal));

    private static void AddCommonWarnings(DeploymentPlan plan, SteamGame game, ProxySelectionResult proxy)
    {
        if (game.RequiresConfirmation)
            plan.Warnings.Add("Anti-cheat files were detected. Explicit compatibility confirmation is required.");
    }

    private static void AddManifest(DeploymentPlan plan, SteamGame game) => plan.Operations.Add(new(
        DeploymentOperationType.WriteManifest, Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json")));

    private static bool ManagedReShadePresetMissing(SteamGame game, StackSnapshot snapshot)
    {
        var reshade = snapshot.ReportFor(ComponentKind.ReShade);
        if (reshade is null) return false;
        if (reshade.State is not (ComponentLifecycleState.InstalledHealthy or
            ComponentLifecycleState.InstalledWithWarnings or
            ComponentLifecycleState.InstalledMetadataIncomplete or
            ComponentLifecycleState.UpdateAvailable or
            ComponentLifecycleState.RepairRequired or
            ComponentLifecycleState.RepairRecommended))
            return false;
        if (reshade.Ownership is not (OwnershipHealth.Managed or OwnershipHealth.Incomplete or OwnershipHealth.Migrated))
            return false;
        return !File.Exists(ReShadePresetService.CleanPresetPath(game));
    }

    private static DeploymentPlan NewPlan(SteamGame game, string action) => new()
    {
        Id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}",
        AppId = game.AppId,
        GameRoot = game.GameRoot,
        DeploymentDirectory = game.DeploymentDirectory,
        Action = action
    };

    private static void ValidateBundle(SteamGame game, RecommendedStackArtifacts artifacts)
    {
        if (artifacts.ReShade is null && artifacts.RenoDx is null && artifacts.OptiScaler is null)
            throw new InvalidOperationException("No component was selected for installation.");
        if (artifacts.RenoDx is not null && artifacts.ReShade is null)
            throw new InvalidOperationException("RenoDX requires a full-addon ReShade artifact.");
        foreach (var artifact in new[] { artifacts.ReShade, artifacts.RenoDx, artifacts.OptiScaler }.OfType<ComponentArtifact>())
            ValidateArtifact(game, artifact);
    }

    private static async Task ValidateDigestAsync(ComponentArtifact artifact, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(artifact.Sha256)) return;
        var actual = await ArtifactDownloader.Sha256Async(artifact.Path, token);
        if (!actual.Equals(artifact.Sha256.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact SHA-256 does not match the expected digest.");
    }

    private static void ValidateProxy(string proxyName)
    {
        if (!SupportedProxyNames.Contains(proxyName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported proxy name: {proxyName}");
    }

    private static DeploymentConflictException BuildProxyConflict(
        ProxySelectionResult selection,
        SteamGame game,
        ComponentKind component)
    {
        var lines = new List<string>
        {
            $"Required write: {component} must occupy one supported proxy filename in {game.DeploymentDirectory}.",
            "Alternatives tested:"
        };
        foreach (var candidate in selection.Candidates)
        {
            var rejection = selection.RejectedAlternatives.FirstOrDefault(item =>
                item.StartsWith(candidate.ProxyName + ":", StringComparison.OrdinalIgnoreCase)) ?? candidate.Reason;
            lines.Add($"- {candidate.Path}: {candidate.Classification}; {rejection}");
            if (candidate.Evidence.Count > 0) lines.Add($"  Evidence: {string.Join("; ", candidate.Evidence)}");
        }
        lines.Add("Every supported proxy alternative was rejected; no existing DLL will be overwritten.");
        return new DeploymentConflictException(string.Join(Environment.NewLine, lines));
    }

    private static void ValidateArtifact(SteamGame game, ComponentArtifact artifact)
    {
        if (!File.Exists(artifact.Path)) throw new FileNotFoundException("Artifact does not exist.", artifact.Path);
        var extension = Path.GetExtension(artifact.FileName);
        if (artifact.Component == ComponentKind.RenoDx && extension is not ".addon64" and not ".addon32")
            throw new InvalidDataException("RenoDX artifacts must retain their .addon64 or .addon32 filename.");
        if (artifact.Component == ComponentKind.OptiPatcher)
            throw new NotSupportedException("OptiPatcher is legacy-cleanup-only and cannot be deployed.");
        var artifactArchitecture = ArtifactValidator.ValidatePe(artifact.Path);
        var gameArchitecture = SelectedArchitecture(game);
        if (artifact.Component == ComponentKind.RenoDx)
        {
            var fileArchitecture = extension == ".addon32" ? PeArchitecture.X86 : PeArchitecture.X64;
            if (artifactArchitecture != fileArchitecture)
                throw new InvalidDataException(
                    $"RenoDX filename requires {fileArchitecture}, but the payload is {artifactArchitecture}.");
        }
        if (artifact.Component == ComponentKind.OptiScaler && artifactArchitecture != PeArchitecture.X64)
            throw new InvalidDataException("OptiScaler requires an x64 payload.");
        if (gameArchitecture is not PeArchitecture.Unknown && artifactArchitecture != gameArchitecture)
            throw new InvalidDataException(
                $"{artifact.Component} payload architecture is {artifactArchitecture}, but the selected game is {gameArchitecture}.");
    }

    private static PeArchitecture SelectedArchitecture(SteamGame game)
    {
        var candidate = game.Candidates.FirstOrDefault(item => game.Executable is not null &&
            Path.GetFullPath(item.Path).Equals(Path.GetFullPath(game.Executable), StringComparison.Ordinal));
        if (candidate is not null) return candidate.Architecture;
        if (game.Executable is null || !File.Exists(game.Executable)) return PeArchitecture.Unknown;
        try { return ArtifactValidator.ValidatePe(game.Executable); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return PeArchitecture.Unknown;
        }
    }

    private static string NormalizedHash(ComponentArtifact artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
            return artifact.Sha256.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase);
        using var stream = File.OpenRead(artifact.Path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashPath(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool TryGetTrustedBackup(
        SteamGame game,
        ManagedFile file,
        out string? backupPath,
        out string? error)
    {
        backupPath = null;
        error = null;
        if (file.BackupRelativePath is null) return false;
        if (file.BackupSha256 is not { Length: 64 } || !file.BackupSha256.All(Uri.IsHexDigit))
        {
            error = $"Original backup for {file.RelativePath} is not exposed because its legacy ownership record has no trusted SHA-256 digest.";
            return false;
        }
        backupPath = ResolveGamePath(game, file.BackupRelativePath);
        if (!File.Exists(backupPath))
        {
            error = $"Original backup for {file.RelativePath} is missing: {file.BackupRelativePath}";
            backupPath = null;
            return false;
        }
        if (!HashPath(backupPath).Equals(file.BackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Original backup for {file.RelativePath} changed after it was recorded and will not be restored automatically.";
            backupPath = null;
            return false;
        }
        return true;
    }

    private static string ResolveAddonDirectory(string deploymentDirectory, string gameRoot)
    {
        var ini = Path.Combine(deploymentDirectory, "ReShade.ini");
        if (!File.Exists(ini)) ini = Path.Combine(deploymentDirectory, "reshade.ini");
        if (!File.Exists(ini)) return deploymentDirectory;
        var value = IniDocument.Parse(File.ReadAllText(ini)).Get("ADDON", "AddonPath");
        if (string.IsNullOrWhiteSpace(value)) return deploymentDirectory;
        var normalized = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(deploymentDirectory, normalized));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        if (!resolved.Equals(normalizedRoot, StringComparison.Ordinal) &&
            !resolved.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("ReShade ADDON.AddonPath resolves outside the selected game directory.");
        return resolved;
    }
}
