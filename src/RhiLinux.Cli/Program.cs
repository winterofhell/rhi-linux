using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Mods;
using RhiLinux.Steam;

return await CliApplication.RunAsync(args);

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { PrintHelp(); return 0; }
            var command = args[0].ToLowerInvariant();
            var diagnosticsReadOnly = command == "diagnostics";
            var cacheAction = command == "cache" && args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1].ToLowerInvariant() : null;
            var options = Options.Parse(args[(cacheAction is null ? 1 : 2)..]);
            var xdg = new XdgPaths();
            if (command == "cache") return await HandleCache(cacheAction, options, xdg);
            var statePath = options.Value("state") ?? xdg.StateFile;
            IStateStore store = new JsonStateStore(statePath);
            var state = await store.LoadAsync();
            var discovery = new SteamDiscoveryService(new ExecutableDetector());

            if (command == "scan")
            {
                var roots = options.Values("steam-root");
                var result = await discovery.ScanAsync(roots.Count == 0 ? null : roots, state.Overrides,
                    includeDefaultRoots: roots.Count > 0);
                state.DiscoveredGames = result.Games.ToList(); state.LastScanUtc = DateTimeOffset.UtcNow;
                if (!options.Has("no-save") && !diagnosticsReadOnly) await store.SaveAsync(state);
                Write(result, options.Has("json"), options.Has("diagnostics") ? PrintDiagnosticScan : PrintScan);
                return result.Warnings.Count == 0 ? 0 : 2;
            }

            var configuredRoots = options.Values("steam-root");
            var currentScan = await discovery.ScanAsync(configuredRoots.Count == 0 ? null : configuredRoots, state.Overrides,
                includeDefaultRoots: configuredRoots.Count > 0);
            state.DiscoveredGames = currentScan.Games.ToList();
            state.LastScanUtc = DateTimeOffset.UtcNow;
            if (!options.Has("no-save") && !diagnosticsReadOnly) await store.SaveAsync(state);
            if (command == "games") { Write(state.DiscoveredGames, options.Has("json"), PrintGames); return 0; }

            var game = SelectGame(state.DiscoveredGames, options.Value("game"));
            if (options.Value("exe") is { } executableOverride)
            {
                state.Overrides[game.AppId] = new GameOverride(executableOverride, options.Value("deployment-dir"));
                if (!diagnosticsReadOnly) await store.SaveAsync(state);
                var roots = options.Values("steam-root");
                var refreshed = await discovery.ScanAsync(roots.Count == 0 ? [game.SteamRoot] : roots, state.Overrides);
                game = refreshed.Games.Single(x => x.AppId == game.AppId);
                state.DiscoveredGames.RemoveAll(x => x.AppId == game.AppId); state.DiscoveredGames.Add(game);
                if (!diagnosticsReadOnly) await store.SaveAsync(state);
            }

            switch (command)
            {
                case "status":
                    var statuses = await new ComponentDetector().DetectAsync(game);
                    Write(statuses, options.Has("json"), value => PrintStatuses(game, value));
                    return statuses.Any(x => x.Health is ComponentHealth.Broken or ComponentHealth.Conflicting) ? 3 : 0;
                case "profile":
                    var profile = await new GameProfileCatalog(xdg).MatchAsync(game);
                    Write(profile, options.Has("json"), value => PrintProfile(game, value));
                    return profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported ? 3 : 0;
                case "proxy-diagnostics":
                    var diagnostics = await new ProxyDiagnosticsService(new GameProfileCatalog(xdg)).DiagnoseAsync(game);
                    Write(diagnostics, options.Has("json"), value => PrintProxyDiagnostics(game, value));
                    return diagnostics.HasSafeProxy ? 0 : 3;
                case "artifacts":
                    var artifacts = await new OfficialArtifactResolver(new HttpClient(), xdg).ResolveAsync(game, !options.Has("offline"));
                    Write(artifacts, options.Has("json"), value => PrintArtifacts(game, value));
                    return artifacts.IsFullyAutomatic ? 0 : 3;
                case "stack-status":
                    return await PrintStackStatus(game, options, xdg);
                case "diagnostics":
                    return await PrintDiagnostics(game, options, xdg);
                case "candidates":
                    Write(game.Candidates, options.Has("json"), value => PrintCandidates(game, value));
                    return 0;
                case "launch-option":
                    var launchOption = DeploymentPlanner.GenerateLaunchOption(options.Value("proxy") ?? "dxgi.dll");
                    if (options.Has("json")) Console.WriteLine(JsonSerializer.Serialize(new { game.AppId, launchOption }, JsonOptions)); else Console.WriteLine(launchOption);
                    return 0;
                case "plan":
                case "install":
                case "update":
                case "repair":
                    return await HandleInstallLike(command, game, options, state, store);
                case "remove":
                    return await HandleRemove(game, options, state, store);
                case "restore":
                    return await ExecutePlan(await new DeploymentPlanner().BuildRestorePlanAsync(game), options, state, store);
                default: throw new ArgumentException($"Unknown command '{command}'. Run 'help' for usage.");
            }
        }
        catch (DeploymentConflictException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            Console.Error.WriteLine($"technical details: {exception.TechnicalDetails}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> HandleInstallLike(string command, SteamGame game, Options options, ApplicationState state, IStateStore store)
    {
        if (command == "plan" && options.Has("recommended"))
        {
            var paths = new XdgPaths();
            var catalog = new GameProfileCatalog(paths);
            var resolution = await new OfficialArtifactResolver(new HttpClient(), paths, catalog).AcquireAsync(game, !options.Has("offline"));
            var proxy = await new ProxyDiagnosticsService(catalog).DiagnoseAsync(game);
            var recommendedPlan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game,
                await ToRecommendedArtifactsAsync(resolution, paths), proxy);
            return await ExecutePlan(recommendedPlan, options, state, store);
        }
        var component = ParseComponent(options.Required("component"));
        var sourceUrl = options.Value("source-url");
        string source;
        string fileName;
        if (options.Value("source") is { } localSource && localSource != "true")
        {
            source = Path.GetFullPath(localSource);
            fileName = options.Value("artifact-name") ?? Path.GetFileName(source);
        }
        else if (sourceUrl is not null && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            fileName = options.Value("artifact-name") ?? Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Remote sources require --artifact-name.");
            source = await new ArtifactDownloader(new HttpClient(), new XdgPaths()).DownloadAsync(uri, fileName, options.Value("sha256"));
        }
        else throw new ArgumentException("Supply --source FILE or an official --source-url URL.");
        var artifact = new ComponentArtifact(component, source, fileName, options.Value("version"), sourceUrl, options.Value("sha256"));
        if (!options.Has("dry-run") && options.Has("apply")) ArtifactValidator.ValidatePe(source);
        var planner = new DeploymentPlanner();
        var plan = command == "repair"
            ? await planner.BuildRepairPlanAsync(game, component, artifact, options.Value("proxy") ?? "dxgi.dll")
            : await planner.BuildInstallPlanAsync(game, artifact, options.Value("proxy") ?? "dxgi.dll");
        return await ExecutePlan(plan, options, state, store);
    }

    private static async Task<int> HandleRemove(SteamGame game, Options options, ApplicationState state, IStateStore store)
    {
        var component = ParseComponent(options.Required("component"));
        var plan = await new DeploymentPlanner().BuildRemovePlanAsync(game, component, options.Value("proxy") ?? "dxgi.dll");
        return await ExecutePlan(plan, options, state, store);
    }

    private static async Task<int> HandleCache(string? action, Options options, XdgPaths paths)
    {
        var cache = new ArtifactCacheService(paths);
        switch (action)
        {
            case "list":
            case "verify":
                var entries = await cache.ListAsync();
                Write(entries, options.Has("json"), value =>
                {
                    foreach (var entry in value)
                        Console.WriteLine($"{entry.Metadata.Component,-12} {entry.Metadata.Version,-14} {(entry.IsValid ? "valid" : "INVALID"),-8} {entry.Metadata.AssetName}{(entry.Problem is null ? string.Empty : $" — {entry.Problem}")}");
                });
                return entries.All(x => x.IsValid) ? 0 : 3;
            case "clean":
                var removed = await cache.CleanInvalidAsync();
                if (options.Has("json")) Console.WriteLine(JsonSerializer.Serialize(new { removed }, JsonOptions));
                else Console.WriteLine($"Removed {removed} invalid artifact cache entr{(removed == 1 ? "y" : "ies")}.");
                return 0;
            default: throw new ArgumentException("Use 'cache list', 'cache verify', or 'cache clean'.");
        }
    }

    private static async Task<int> PrintStackStatus(SteamGame game, Options options, XdgPaths paths)
    {
        var report = await new StackStatusService(new HttpClient(), paths).GetAsync(game, !options.Has("offline"));
        if (options.Has("json")) Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        else
        {
            PrintProfile(game, report.Profile);
            PrintProxyDiagnostics(game, report.Proxy);
            PrintArtifacts(game, report.ArtifactResolution);
            PrintStatuses(game, report.Components);
            Console.WriteLine($"OptiScaler eligibility: {report.OptiScalerEligibility.Level} — {report.OptiScalerEligibility.Explanation}");
            Console.WriteLine($"Stack: {report.Summary}");
        }
        return report.CanInstallRecommendedStack ? 0 : 3;
    }

    private static async Task<int> PrintDiagnostics(SteamGame game, Options options, XdgPaths paths)
    {
        var allowNetwork = !options.Has("offline");
        using var httpClient = new HttpClient();
        var statusService = new StackStatusService(httpClient, paths);
        var report = await statusService.GetAsync(game, allowNetwork);
        var resolution = report.ArtifactResolution;
        if (options.Has("download"))
        {
            var requestedComponents = new HashSet<ComponentKind>();
            if (resolution.CanAcquireRenoSetup)
            {
                requestedComponents.Add(ComponentKind.ReShade);
                requestedComponents.Add(ComponentKind.RenoDx);
            }
            if (resolution.CanAcquireOptiScaler) requestedComponents.Add(ComponentKind.OptiScaler);
            resolution = await new OfficialArtifactResolver(httpClient, paths).AcquireSelectedAsync(
                resolution, requestedComponents, allowNetwork);
            report = await statusService.GetAsync(game, false);
            report = report with { ArtifactResolution = resolution };
        }

        resolution = RestrictToCachedTargets(resolution);
        report = report with { ArtifactResolution = resolution };

        DeploymentPlan? plan = null;
        string planStatus;
        string? conflictDetails = null;
        if (!report.CanInstallRecommendedStack)
        {
            planStatus = $"No recommended plan is available: {report.Summary}";
        }
        else if (!RequiredRecommendedArtifactsAreCached(resolution))
        {
            planStatus = "The recommended plan was not built because one or more selected official artifacts are not cached. " +
                "No artifact payload was downloaded; rerun with --download to populate the cache explicitly.";
        }
        else
        {
            try
            {
                plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(
                    game,
                    await ToRecommendedArtifactsAsync(resolution, paths),
                    report.Proxy);
                planStatus = "Dry-run plan built successfully. No deployment operations were executed.";
            }
            catch (DeploymentConflictException exception)
            {
                planStatus = exception.Message;
                conflictDetails = exception.TechnicalDetails;
            }
            catch (InvalidOperationException exception)
            {
                planStatus = $"The cached artifacts could not produce a plan: {exception.Message}";
            }
        }

        var diagnostics = new DiagnosticsReport(report with { ArtifactResolution = resolution }, plan, planStatus,
            options.Has("download"));
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(diagnostics, JsonOptions));
        }
        else
        {
            Console.WriteLine($"Release diagnostics for {game.Name} ({game.AppId})");
            PrintProfile(game, report.Profile);
            PrintProxyDiagnostics(game, report.Proxy);
            PrintArtifacts(game, resolution);
            PrintStatuses(game, report.Components);
            Console.WriteLine($"OptiScaler eligibility: {report.OptiScalerEligibility.Level} — {report.OptiScalerEligibility.Explanation}");
            Console.WriteLine($"Stack: {report.Summary}");
            Console.WriteLine("Recommended dry-run plan:");
            if (plan is null) Console.WriteLine($"  {planStatus}");
            else
            {
                Console.WriteLine($"  {planStatus}");
                PrintPlan(plan);
            }
        }

        if (conflictDetails is not null)
            Console.Error.WriteLine($"technical details: {conflictDetails}");
        return report.CanInstallRecommendedStack && planStatus.StartsWith("Dry-run plan built", StringComparison.Ordinal)
            ? 0
            : 3;
    }

    private static bool RequiredRecommendedArtifactsAreCached(GameArtifactResolution resolution)
    {
        var required = new List<ComponentKind>();
        if (resolution.CanAcquireRenoSetup)
        {
            required.Add(ComponentKind.ReShade);
            required.Add(ComponentKind.RenoDx);
        }
        if (resolution.CanAcquireOptiScaler) required.Add(ComponentKind.OptiScaler);
        return required.Count > 0 && required.All(component => resolution.Artifacts.Any(artifact =>
            artifact.Component == component && artifact.CacheState == ArtifactCacheState.Cached));
    }

    private static GameArtifactResolution RestrictToCachedTargets(GameArtifactResolution resolution)
    {
        bool IsCached(ComponentKind component) => resolution.Artifacts.Any(artifact =>
            artifact.Component == component && artifact.CacheState == ArtifactCacheState.Cached &&
            artifact.Validation == ArtifactValidationState.Valid);
        var renoReady = resolution.CanAcquireRenoSetup &&
            IsCached(ComponentKind.ReShade) && IsCached(ComponentKind.RenoDx);
        var optiReady = resolution.CanAcquireOptiScaler && IsCached(ComponentKind.OptiScaler);
        return resolution with
        {
            CanAcquireRenoSetup = renoReady,
            CanAcquireOptiScaler = optiReady,
            IsFullyAutomatic = renoReady || optiReady
        };
    }

    private static async Task<RecommendedStackArtifacts> ToRecommendedArtifactsAsync(
        GameArtifactResolution resolution,
        XdgPaths paths)
    {
        ComponentArtifact Required(ComponentKind component)
        {
            var selection = resolution.Artifacts.SingleOrDefault(x => x.Component == component && x.CachedPath is not null)
                ?? throw new InvalidOperationException($"The required {component} artifact is not cached.");
            return CachedArtifactMaterializer.MaterializeSingle(selection);
        }
        var opti = resolution.Artifacts.SingleOrDefault(x => x.Component == ComponentKind.OptiScaler);
        (ComponentArtifact Main, IReadOnlyList<ComponentArtifact> Support, OptiScalerBundleManifest Manifest)? materializedOpti = null;
        if (opti is not null)
            materializedOpti = await CachedArtifactMaterializer.MaterializeOptiScalerAsync(new ArtifactCacheService(paths), opti);
        var includeReno = resolution.CanAcquireRenoSetup;
        var includeOpti = resolution.CanAcquireOptiScaler;
        return new(
            includeReno ? Required(ComponentKind.ReShade) : null,
            includeReno ? Required(ComponentKind.RenoDx) : null,
            includeOpti ? materializedOpti?.Main : null,
            includeOpti ? materializedOpti?.Support : null,
            includeOpti ? materializedOpti?.Manifest : null);
    }

    private static async Task<int> ExecutePlan(DeploymentPlan plan, Options options, ApplicationState state, IStateStore store)
    {
        var apply = options.Has("apply") && !options.Has("dry-run");
        if (apply && !options.Has("yes")) throw new InvalidOperationException("Filesystem changes require both --apply and --yes after reviewing the plan.");
        if (apply && plan.Warnings.Any(x => x.Contains("Anti-cheat", StringComparison.OrdinalIgnoreCase)) && !options.Has("allow-anti-cheat"))
            throw new InvalidOperationException("Anti-cheat confirmation requires --allow-anti-cheat.");
        if (options.Has("json")) Console.WriteLine(JsonSerializer.Serialize(plan, JsonOptions)); else PrintPlan(plan);
        var started = DateTimeOffset.UtcNow;
        var result = await new DeploymentExecutor().ExecuteAsync(plan, !apply);
        state.Transactions.Add(new TransactionRecord(plan.Id, plan.AppId, plan.Action, started, DateTimeOffset.UtcNow, result.RolledBack, result.Messages, result.Error));
        await store.SaveAsync(state);
        if (!options.Has("json"))
            Console.WriteLine(result.DryRun
                ? "Dry run complete; no game files changed."
                : result.Succeeded
                    ? "Deployment completed."
                    : result.RolledBack
                        ? $"Deployment failed; completed changes were rolled back: {result.Error}"
                        : $"Deployment failed: {result.Error}");
        return result.Succeeded ? 0 : 4;
    }

    private static SteamGame SelectGame(IReadOnlyList<SteamGame> games, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            if (games.Count == 1) return games[0];
            throw new ArgumentException("Select a game with --game <AppID-or-name>.");
        }
        var matches = uint.TryParse(selector, out var appId)
            ? games.Where(x => x.AppId == appId).ToArray()
            : games.Where(x => x.Name.Contains(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch { 1 => matches[0], 0 => throw new ArgumentException($"Game '{selector}' was not found."), _ => throw new ArgumentException($"Game selector '{selector}' is ambiguous.") };
    }

    private static ComponentKind ParseComponent(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "reshade" => ComponentKind.ReShade,
        "renodx" => ComponentKind.RenoDx,
        "optiscaler" => ComponentKind.OptiScaler,
        _ => throw new ArgumentException($"Unknown component '{value}'.")
    };
    private static void Write<T>(T value, bool json, Action<T> textWriter)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions)); else textWriter(value);
    }
    private static void PrintScan(ScanResult result)
    {
        Console.WriteLine($"Steam roots: {result.SteamRoots.Count}; libraries: {result.Libraries.Count}; installed games: {result.Games.Count}");
        PrintGames(result.Games);
        foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {warning}");
    }

    private static void PrintDiagnosticScan(ScanResult result)
    {
        PrintScan(result);
        Console.WriteLine($"Steam roots/libraries diagnosed: {result.RootDiagnostics?.Count ?? 0}");
        foreach (var root in result.RootDiagnostics ?? [])
            Console.WriteLine(
                $"{root.Source,-12} exists={root.Exists} readable={root.Readable} dedupe={root.Deduplicated} " +
                $"manifests={root.ManifestsFound} games={root.GamesIncluded} | original={root.OriginalPath} | " +
                $"canonical={root.CanonicalPath}" +
                (root.SkipReason is null ? string.Empty : $" | {root.SkipReason}"));
        Console.WriteLine($"Appmanifests: {result.ManifestDiagnostics?.Count ?? 0}");
        foreach (var diagnostic in result.ManifestDiagnostics ?? [])
            Console.WriteLine($"{diagnostic.Disposition,-10} {diagnostic.AppId?.ToString() ?? "unknown",-10} " +
                $"{diagnostic.Name ?? "unknown"} | flags={diagnostic.StateFlags ?? "unknown"} | " +
                $"installdir={diagnostic.InstallDirectory ?? "unknown"} | library={diagnostic.LibraryRoot} | " +
                $"manifest={diagnostic.ManifestPath} | {diagnostic.Reason}");
    }
    private static void PrintGames(IReadOnlyList<SteamGame> games)
    {
        foreach (var game in games) Console.WriteLine($"{game.AppId,-10} {game.Name} | {game.Engine} | {game.Confidence} | {game.Executable}");
    }
    private static void PrintCandidates(SteamGame game, IReadOnlyList<ExecutableCandidate> candidates)
    {
        Console.WriteLine($"{game.Name} ({game.AppId})");
        Console.WriteLine($"Selected deployment directory: {game.DeploymentDirectory}");
        foreach (var candidate in candidates) Console.WriteLine($"{candidate.Score,4} {candidate.Confidence,-6} {candidate.Architecture,-7} {candidate.Path}\n     {string.Join("; ", candidate.Reasons)}");
    }
    private static void PrintStatuses(SteamGame game, IReadOnlyList<ComponentStatus> statuses)
    {
        Console.WriteLine($"{game.Name} ({game.AppId}) — {game.DeploymentDirectory}");
        foreach (var status in statuses) Console.WriteLine($"{status.Component,-12} {status.Health,-11} {status.Explanation}");
    }
    private static void PrintProfile(SteamGame game, GameProfileMatch match)
    {
        Console.WriteLine($"{game.Name} ({game.AppId}) — profile {match.Profile.Id}");
        Console.WriteLine($"Game root: {game.GameRoot}");
        Console.WriteLine($"Executable: {game.Executable ?? "not detected"}");
        Console.WriteLine($"Deployment directory: {game.DeploymentDirectory}");
        Console.WriteLine($"Proton prefix: {game.ProtonPrefix}");
        Console.WriteLine($"Match: {match.MatchReason}");
        Console.WriteLine($"Executable override: {match.Profile.ExecutableRelativePath ?? "automatic"}");
        Console.WriteLine($"Deployment override: {match.Profile.DeploymentRelativePath ?? "automatic"}");
        Console.WriteLine($"RenoDX: {match.Profile.RenoDxSupport} — {match.Profile.RenoDx?.FileName ?? "no artifact mapping"}");
        if (match.Profile.RenoDxSupport == GameProfileSupport.Unsupported)
            Console.WriteLine("Note: profile-only Unsupported means no built-in profile; wiki resolution may still find an exact addon.");
        Console.WriteLine($"OptiScaler profile evidence: {(match.Profile.OptiScalerCompatible ? "known compatible" : "no game-specific signal")}");
    }
    private static void PrintProxyDiagnostics(SteamGame game, ProxySelectionResult result)
    {
        Console.WriteLine($"Proxy diagnostics for {game.Name} ({game.AppId})");
        foreach (var candidate in result.Candidates)
            Console.WriteLine($"{candidate.ProxyName,-12} {candidate.Classification,-36} score {candidate.Score,4} " +
                $"prohibited={candidate.ProhibitedByProfile,-5} override={candidate.ProtonOverrideAppropriate,-5} {candidate.Reason}\n             {string.Join("; ", candidate.Evidence)}");
        Console.WriteLine($"Selected: {result.SelectedProxy ?? "none"} — {result.Reason}");
        if (result.LaunchOption is not null) Console.WriteLine($"Launch option: {result.LaunchOption}");
        Console.WriteLine("Rejected proxy alternatives:");
        if (result.RejectedAlternatives.Count == 0) Console.WriteLine("  none");
        else foreach (var alternative in result.RejectedAlternatives) Console.WriteLine($"  - {alternative}");
    }
    private static void PrintArtifacts(SteamGame game, GameArtifactResolution resolution)
    {
        Console.WriteLine($"Official artifacts for {game.Name} ({game.AppId})");
        foreach (var artifact in resolution.Artifacts)
            Console.WriteLine($"{artifact.Component,-12} {artifact.Version,-14} {artifact.CacheState,-16} {artifact.AssetName}\n             {artifact.SourceUrl}");
        Console.WriteLine($"RenoDX compatibility: {resolution.RenoDxCompatibility}");
        if (resolution.RenoDxMatch is { } match)
            Console.WriteLine($"RenoDX match: {match.MatchType} confidence={match.Confidence:0.00} reason={match.ReasonCode}");
        if (resolution.SuggestedExecutable is { } suggested)
            Console.WriteLine($"Suggested executable: {suggested}");
        Console.WriteLine($"Can acquire RenoDX: {resolution.CanAcquireRenoDx}; Reno setup: {resolution.CanAcquireRenoSetup}");
        foreach (var warning in resolution.Warnings) Console.WriteLine($"WARNING: {warning}");
        Console.WriteLine($"Fully automatic: {resolution.IsFullyAutomatic}");
    }
    private static void PrintPlan(DeploymentPlan plan)
    {
        Console.WriteLine($"Plan {plan.Id}: {plan.Action} for AppID {plan.AppId}");
        Console.WriteLine($"Deployment directory: {plan.DeploymentDirectory}");
        var changed = plan.FileDecisions.Count(x => x.Action is DeploymentFileAction.Deploy or
            DeploymentFileAction.ReplaceManaged or DeploymentFileAction.RemoveManaged or DeploymentFileAction.MoveManaged);
        var preserved = plan.FileDecisions.Count(x => x.PreservesExistingFile);
        var replaced = plan.FileDecisions.Count(x => x.ReplacesManagedFile);
        Console.WriteLine($"Confirmation: {changed} file change(s), {preserved} existing file(s) preserved, " +
            $"{replaced} managed file(s) replaced; {plan.Operations.Count} transactional step(s).");
        Console.WriteLine($"Compatibility: {plan.CompatibilityMessage ?? "No additional compatibility note."}");
        Console.WriteLine($"Launch option: {plan.LaunchOption ?? "No Proton DLL override is required."}");
        foreach (var warning in plan.Warnings) Console.WriteLine($"WARNING: {warning}");
        Console.WriteLine("File decisions:");
        if (plan.FileDecisions.Count == 0) Console.WriteLine("  none");
        foreach (var decision in plan.FileDecisions)
        {
            Console.WriteLine($"  [{decision.Requirement}/{decision.Action}] {decision.Component}");
            Console.WriteLine($"    Classification: {DescribeDecisionClassification(decision)}");
            Console.WriteLine($"    Reason: {decision.Reason}");
            Console.WriteLine($"    Source release: {decision.SourceRelease ?? "not applicable"}");
            Console.WriteLine($"    Archive path: {decision.SourceArchivePath ?? "not applicable"}");
            Console.WriteLine($"    Destination: {decision.DestinationPath}");
            Console.WriteLine($"    Replaces managed file: {decision.ReplacesManagedFile}; preserves existing file: {decision.PreservesExistingFile}");
            Console.WriteLine($"    Alternative exists: {decision.AlternativeExists}; alternatives: " +
                (decision.Alternatives.Count == 0 ? "none" : string.Join(", ", decision.Alternatives)));
        }
        Console.WriteLine("Transactional operations:");
        for (var i = 0; i < plan.Operations.Count; i++) Console.WriteLine($"{i + 1,2}. {DeploymentExecutor.Describe(plan.Operations[i])}");
    }

    private static string DescribeDecisionClassification(DeploymentFileDecision decision) => decision.Requirement switch
    {
        DeploymentFileRequirement.SuppliedByGame => "game-supplied; preserve in place",
        DeploymentFileRequirement.NeverReplace => "protected game file; never replace",
        DeploymentFileRequirement.ObsoleteManaged => "obsolete RHI-managed file; remove transactionally",
        DeploymentFileRequirement.UnrelatedArchiveContent => "unrelated archive content; do not deploy",
        DeploymentFileRequirement.Optional when decision.Action == DeploymentFileAction.Omit => "optional feature file; omitted",
        DeploymentFileRequirement.Conditional when decision.Action == DeploymentFileAction.Omit => "conditional feature file; not required for this game",
        _ when decision.Action == DeploymentFileAction.AdoptOfficial => "official file already present; adopt without replacing",
        _ when decision.PreservesExistingFile => "existing file preserved",
        _ when decision.ReplacesManagedFile => "RHI-managed file replaced with backup and rollback support",
        _ => "selected official deployment file"
    };
    private static void PrintHelp() => Console.WriteLine("""
        RHI Linux — Steam Proton mod deployment manager

        Commands:
          scan [--steam-root PATH] [--diagnostics] [--json] [--no-save] [--state PATH]
          games [--json]
          status --game APPID|NAME [--json]
          profile --game APPID|NAME [--json]
          artifacts --game APPID|NAME [--offline] [--json]
          proxy-diagnostics --game APPID|NAME [--json]
          stack-status --game APPID|NAME [--offline] [--json]
          diagnostics --game APPID|NAME [--offline] [--download] [--json]
          candidates --game APPID|NAME [--exe RELATIVE_PATH]
          plan --game GAME --recommended [--offline]
          plan|install|update|repair --game GAME --component NAME (--source FILE | --source-url URL) [--proxy NAME]
          remove --game GAME --component NAME [--proxy NAME]
          restore --game GAME
          launch-option --game GAME [--proxy NAME]
          cache list|verify|clean [--json]

        File-changing commands are dry runs unless both --apply and --yes are supplied.
        Use --allow-anti-cheat for games where anti-cheat markers were detected.
        Diagnostics never applies a plan or writes application/game state. --download explicitly permits official artifact cache downloads.
        """);

    private sealed record DiagnosticsReport(
        StackStatusReport Stack,
        DeploymentPlan? RecommendedPlan,
        string RecommendedPlanStatus,
        bool ArtifactDownloadRequested);

    private sealed class Options(Dictionary<string, List<string>> values)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument: {args[i]}");
                var key = args[i][2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                if (!values.TryGetValue(key, out var list)) values[key] = list = [];
                list.Add(value);
            }
            return new Options(values);
        }
        public bool Has(string key) => values.ContainsKey(key);
        public string? Value(string key) => values.TryGetValue(key, out var list) ? list[^1] : null;
        public IReadOnlyList<string> Values(string key) => values.TryGetValue(key, out var list) ? list : [];
        public string Required(string key) => Value(key) is { } value && value != "true" ? value : throw new ArgumentException($"Missing --{key} value.");
    }
}
