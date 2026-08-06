using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed class GameReadinessService : IGameReadinessService
{
    public const string RulesVersion = "1";

    private readonly StackStatusService? stackStatus;
    private readonly ComponentDetector detector;
    private readonly ProxyDiagnosticsService proxyDiagnostics;
    private readonly IRecommendedSetupService recommendations;
    private readonly ILaunchConfigurationProvider? launchConfiguration;
    private readonly ConcurrentDictionary<string, GameReadinessResult> cache = new(StringComparer.Ordinal);

    public GameReadinessService(
        StackStatusService? stackStatus = null,
        ComponentDetector? detector = null,
        ProxyDiagnosticsService? proxyDiagnostics = null,
        IRecommendedSetupService? recommendations = null,
        ILaunchConfigurationProvider? launchConfiguration = null)
    {
        this.stackStatus = stackStatus;
        this.detector = detector ?? new ComponentDetector();
        this.proxyDiagnostics = proxyDiagnostics ?? new ProxyDiagnosticsService();
        this.recommendations = recommendations ?? new RecommendedSetupService();
        this.launchConfiguration = launchConfiguration;
    }

    public void Invalidate(GameInstallId installId) => cache.TryRemove(installId.Value, out _);

    public void InvalidateAll() => cache.Clear();

    public async Task<GameReadinessResult> EvaluateAsync(
        InstalledGame game,
        GameReadinessEvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GameReadinessEvaluationOptions();
        var fingerprint = BuildFingerprint(game, options);
        if (!options.ForceRefresh &&
            cache.TryGetValue(game.EffectiveInstallId, out var cached) &&
            cached.EvaluationFingerprint == fingerprint)
            return cached with { FromCache = true };

        try
        {
            var result = await EvaluateCoreAsync(game, options, fingerprint, cancellationToken).ConfigureAwait(false);
            cache[game.EffectiveInstallId] = result;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failed = new GameReadinessResult(
                game.InstallId,
                GameReadinessState.Error,
                "Readiness evaluation failed.",
                "Open Diagnostics and refresh the selected game.",
                game.ToDeploymentTarget(),
                [],
                [
                    new GameReadinessIssue(
                        ReadinessIssueCodes.EvaluationFailed,
                        ReadinessIssueSeverity.Blocking,
                        "Evaluation failed",
                        exception.Message,
                        "open-diagnostics",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["exception"] = exception.GetType().Name
                        })
                ],
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                fingerprint);
            cache[game.EffectiveInstallId] = failed;
            return failed;
        }
    }

    private async Task<GameReadinessResult> EvaluateCoreAsync(
        InstalledGame game,
        GameReadinessEvaluationOptions options,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var issues = new List<GameReadinessIssue>();
        var target = game.ToDeploymentTarget();
        IReadOnlyList<ComponentStatus> components = options.PrefetchedComponents ?? [];
        ProxySelectionResult? proxy = null;
        string? launchOption = options.PrefetchedLaunchOption;

        if (components.Count == 0)
        {
            if (stackStatus is not null)
            {
                var report = await stackStatus.GetAsync(target, options.AllowNetwork, cancellationToken, options.ForceRefresh)
                    .ConfigureAwait(false);
                components = report.Components;
                proxy = report.Proxy;
                launchOption ??= report.Proxy.LaunchOption;
            }
            else
            {
                components = await detector.DetectAsync(target, cancellationToken).ConfigureAwait(false);
                proxy = await proxyDiagnostics.DiagnoseAsync(target, cancellationToken).ConfigureAwait(false);
                launchOption ??= proxy.LaunchOption;
            }
        }
        else
        {
            proxy = await proxyDiagnostics.DiagnoseAsync(target, cancellationToken).ConfigureAwait(false);
            launchOption ??= options.PrefetchedProxyName is { Length: > 0 } proxyName
                ? DeploymentPlanner.GenerateLaunchOption(proxyName)
                : proxy.LaunchOption;
        }

        EvaluateInstallRoot(game, issues);
        EvaluateExecutable(game, options.ManualOverride, issues);
        EvaluateDeploymentDirectory(game, issues);
        EvaluateNativeAndArchitecture(game, issues);
        EvaluateAntiCheat(game, options.WarnBeforeAntiCheatDeployments, issues);
        EvaluateProxyAndComponents(components, proxy, issues);
        EvaluateRecovery(game, issues);
        EvaluatePrefix(game, issues);
        EvaluateStale(game, issues);
        EvaluateArtifacts(components, issues);

        var launch = options.PrefetchedLaunchConfiguration;
        if (launch is null && launchConfiguration is not null && !string.IsNullOrWhiteSpace(launchOption))
        {
            launch = await launchConfiguration.GetAsync(
                game,
                proxy?.SelectedProxy ?? options.PrefetchedProxyName,
                launchOption,
                cancellationToken).ConfigureAwait(false);
        }

        if (launch is { ManualCopyRequired: true, Status: not LaunchOptionStatus.NotRequired })
        {
            issues.Add(new(
                ReadinessIssueCodes.LauncherConfigurationRequired,
                ReadinessIssueSeverity.Warning,
                "Launcher configuration required",
                launch.PlacementInstructions,
                "copy-launch-configuration",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["launcher"] = launch.Launcher.ToString()
                }));
        }

        var setup = recommendations.Build(
            game,
            components,
            proxy,
            options.PreferExistingManagedVersions,
            options.WarnBeforeAntiCheatDeployments);

        var state = ResolveState(game, issues, launch);
        var summary = BuildSummary(game, state, issues);
        var nextAction = BuildNextAction(state, setup, issues, launch);

        return new GameReadinessResult(
            game.InstallId,
            state,
            summary,
            nextAction,
            target,
            components,
            issues,
            launch,
            null,
            setup,
            DateTimeOffset.UtcNow,
            fingerprint);
    }

    private static void EvaluateInstallRoot(InstalledGame game, List<GameReadinessIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(game.GameRoot) || !Directory.Exists(game.GameRoot))
        {
            issues.Add(new(
                ReadinessIssueCodes.InstallRootMissing,
                ReadinessIssueSeverity.Blocking,
                "Install root missing",
                "The game install directory was not found. It may be on a disconnected drive.",
                "open-diagnostics",
                Context(game)));
            return;
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(game.GameRoot).Take(1).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new(
                ReadinessIssueCodes.InsufficientPermissions,
                ReadinessIssueSeverity.Blocking,
                "Install root unreadable",
                "RHI Linux cannot read the game directory with the current permissions.",
                "open-diagnostics",
                Context(game)));
        }
        catch (IOException exception)
        {
            issues.Add(new(
                ReadinessIssueCodes.InstallRootUnreadable,
                ReadinessIssueSeverity.Blocking,
                "Install root unreadable",
                exception.Message,
                "open-diagnostics",
                Context(game)));
        }
    }

    private static void EvaluateExecutable(
        InstalledGame game,
        GameOverride? manualOverride,
        List<GameReadinessIssue> issues)
    {
        if (manualOverride?.Executable is { Length: > 0 } overridePath && !File.Exists(overridePath))
        {
            issues.Add(new(
                ReadinessIssueCodes.ManualOverrideInvalid,
                ReadinessIssueSeverity.Blocking,
                "Manual executable override is invalid",
                "The configured executable override no longer exists. Choose another executable before deployment.",
                "choose-executable",
                Context(game, ("path", overridePath))));
            return;
        }

        if (string.IsNullOrWhiteSpace(game.Executable) || !File.Exists(game.Executable))
        {
            issues.Add(new(
                ReadinessIssueCodes.ExecutableMissing,
                ReadinessIssueSeverity.Blocking,
                "Executable missing",
                "The selected executable no longer exists. Choose another executable before deployment.",
                "choose-executable",
                Context(game)));
            return;
        }

        var highConfidence = game.CandidateList.Count(candidate =>
            candidate.Confidence is DetectionConfidence.High or DetectionConfidence.Medium);
        if (game.Confidence is DetectionConfidence.None or DetectionConfidence.Low && highConfidence >= 2)
        {
            issues.Add(new(
                ReadinessIssueCodes.ExecutableAmbiguous,
                ReadinessIssueSeverity.Blocking,
                "Executable selection is ambiguous",
                "Multiple reasonable Windows executables were found. Confirm the correct one before deployment.",
                "choose-executable",
                Context(game)));
        }
        else if (highConfidence >= 2 && game.Confidence != DetectionConfidence.High)
        {
            issues.Add(new(
                ReadinessIssueCodes.ExecutableAmbiguous,
                ReadinessIssueSeverity.Warning,
                "Multiple executable candidates",
                "Another high-confidence executable is available. Confirm the selection if deployment fails.",
                "choose-executable",
                Context(game)));
        }
    }

    private static void EvaluateDeploymentDirectory(InstalledGame game, List<GameReadinessIssue> issues)
    {
        var directory = game.DeploymentDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            issues.Add(new(
                ReadinessIssueCodes.DeploymentDirectoryMissing,
                ReadinessIssueSeverity.Blocking,
                "Deployment directory missing",
                "No safe deployment directory was determined for proxy DLL installation.",
                "choose-deployment-directory",
                Context(game)));
            return;
        }

        if (!Directory.Exists(directory))
        {
            issues.Add(new(
                ReadinessIssueCodes.DeploymentDirectoryMissing,
                ReadinessIssueSeverity.Blocking,
                "Deployment directory missing",
                "The deployment directory does not exist.",
                "choose-deployment-directory",
                Context(game, ("path", directory))));
        }
    }

    private static void EvaluateNativeAndArchitecture(InstalledGame game, List<GameReadinessIssue> issues)
    {
        if (game.IsNativeLinux || game.Platform == GameBinaryPlatform.Linux || !game.IsActionable)
        {
            issues.Add(new(
                ReadinessIssueCodes.NativeGameUnsupported,
                ReadinessIssueSeverity.Blocking,
                "Unsupported game platform",
                game.UnsupportedReason ?? "This install is not a supported Windows Proton/Wine deployment target.",
                null,
                Context(game)));
        }
    }

    private static void EvaluateAntiCheat(InstalledGame game, bool warnBeforeAntiCheat, List<GameReadinessIssue> issues)
    {
        if (!game.RequiresConfirmation) return;
        issues.Add(new(
            ReadinessIssueCodes.AntiCheatDetected,
            warnBeforeAntiCheat ? ReadinessIssueSeverity.Blocking : ReadinessIssueSeverity.Warning,
            "Anti-cheat detected",
            "Easy Anti-Cheat or BattlEye markers were detected. Deployment requires explicit confirmation and may prevent online play.",
            "review-recommended-setup",
            Context(game)));
    }

    private static void EvaluateProxyAndComponents(
        IReadOnlyList<ComponentStatus> components,
        ProxySelectionResult? proxy,
        List<GameReadinessIssue> issues)
    {
        if (components.Any(item => item.Health == ComponentHealth.ForeignInstallation) ||
            proxy?.Candidates.Any(candidate =>
                candidate is
                {
                    SafeForNewInstallation: false, Classification: ProxyFileClassification.UnknownDll
                    or ProxyFileClassification.KnownThirdPartyInjector
                }) == true)
        {
            issues.Add(new(
                ReadinessIssueCodes.ForeignProxyConflict,
                ReadinessIssueSeverity.Blocking,
                "Foreign compatibility file detected",
                "RHI will not replace an unknown proxy DLL automatically.",
                "open-diagnostics",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["proxy"] = proxy?.SelectedProxy ?? "unknown"
                }));
        }

        if (components.Any(item => item.Health is ComponentHealth.Conflicting or ComponentHealth.Broken
                or ComponentHealth.PartiallyInstalled or ComponentHealth.IncorrectlyConfigured))
        {
            issues.Add(new(
                ReadinessIssueCodes.InstalledComponentConflict,
                ReadinessIssueSeverity.Warning,
                "Installed component needs attention",
                "One or more managed components are broken, conflicting, or incorrectly configured.",
                "review-recommended-setup"));
        }

        if (components.Any(item => item.Health == ComponentHealth.Outdated))
        {
            issues.Add(new(
                ReadinessIssueCodes.ArtifactUpdateAvailable,
                ReadinessIssueSeverity.Information,
                "Component update available",
                "A newer official component release is available for this game.",
                "review-updates"));
        }
    }

    private static void EvaluateRecovery(InstalledGame game, List<GameReadinessIssue> issues)
    {
        var recovery = DeploymentRecoveryProbe.Probe(game.GameRoot);
        if (recovery.HasInterruptedTransaction)
        {
            issues.Add(new(
                ReadinessIssueCodes.InterruptedDeployment,
                ReadinessIssueSeverity.Blocking,
                "Interrupted deployment",
                recovery.Message ?? "An interrupted deployment transaction requires recovery before continuing.",
                "recover-interrupted",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["journals"] = string.Join(',', recovery.JournalIds)
                }));
            issues.Add(new(
                ReadinessIssueCodes.RecoveryRequired,
                ReadinessIssueSeverity.Blocking,
                "Recovery required",
                "Run a confirmed recovery or restore operation before applying a new setup.",
                "restore-backups"));
        }
    }

    private static void EvaluatePrefix(InstalledGame game, List<GameReadinessIssue> issues)
    {
        if (game.Launcher == GameLauncher.Steam && !game.HasProtonPrefix)
        {
            issues.Add(new(
                ReadinessIssueCodes.PrefixMissing,
                ReadinessIssueSeverity.Warning,
                "Proton prefix not created yet",
                "The Proton prefix is not available yet. File deployment can still proceed, but launch verification may be limited.",
                null,
                Context(game)));
        }
    }

    private static void EvaluateStale(InstalledGame game, List<GameReadinessIssue> issues)
    {
        if (!game.IsStale) return;
        issues.Add(new(
            ReadinessIssueCodes.SourceMetadataStale,
            ReadinessIssueSeverity.Warning,
            "Source metadata is stale",
            "Launcher metadata may be out of date. Refresh sources if the game files look wrong.",
            "refresh-readiness",
            Context(game)));
    }

    private static void EvaluateArtifacts(IReadOnlyList<ComponentStatus> components, List<GameReadinessIssue> issues)
    {
        if (components.Any(item =>
                item.Health is ComponentHealth.Unavailable &&
                item.Explanation.Contains("catalog unavailable", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new(
                ReadinessIssueCodes.ArtifactUnavailable,
                ReadinessIssueSeverity.Warning,
                "Artifact catalog unavailable",
                "Official artifact metadata could not be loaded for one or more components.",
                "refresh-component-information"));
        }
    }

    private static GameReadinessState ResolveState(
        InstalledGame game,
        IReadOnlyList<GameReadinessIssue> issues,
        LaunchConfigurationResult? launch)
    {
        if (issues.Any(issue => issue.Code is ReadinessIssueCodes.InstallRootMissing
                or ReadinessIssueCodes.InstallRootUnreadable))
            return GameReadinessState.Unavailable;

        if (issues.Any(issue => issue.Code is ReadinessIssueCodes.NativeGameUnsupported
                or ReadinessIssueCodes.UnsupportedArchitecture
                or ReadinessIssueCodes.FilesystemUnsupported))
            return GameReadinessState.Unsupported;

        if (issues.Any(issue => issue.Code is ReadinessIssueCodes.ExecutableAmbiguous
                or ReadinessIssueCodes.ManualOverrideInvalid) &&
            issues.Any(issue => issue.Severity == ReadinessIssueSeverity.Blocking &&
                issue.Code is ReadinessIssueCodes.ExecutableAmbiguous or ReadinessIssueCodes.ManualOverrideInvalid))
            return GameReadinessState.NeedsUserSelection;

        if (issues.Any(issue => issue.Severity == ReadinessIssueSeverity.Blocking &&
                issue.Code is ReadinessIssueCodes.ExecutableMissing
                    or ReadinessIssueCodes.DeploymentDirectoryMissing
                    or ReadinessIssueCodes.DeploymentDirectoryUnsafe))
            return GameReadinessState.NeedsUserSelection;

        if (issues.Any(issue => issue.Severity == ReadinessIssueSeverity.Blocking))
        {
            if (issues.Any(issue => issue.Code == ReadinessIssueCodes.AntiCheatDetected))
                return GameReadinessState.Unsupported;
            return issues.Any(issue => issue.Code is ReadinessIssueCodes.InterruptedDeployment
                    or ReadinessIssueCodes.RecoveryRequired or ReadinessIssueCodes.ForeignProxyConflict)
                ? GameReadinessState.NeedsConfiguration
                : GameReadinessState.NeedsConfiguration;
        }

        if (launch is { ManualCopyRequired: true, Status: LaunchOptionStatus.Missing or LaunchOptionStatus.NeedsUpdate or LaunchOptionStatus.NotDetected })
            return GameReadinessState.NeedsConfiguration;

        if (issues.Any(issue => issue.Severity == ReadinessIssueSeverity.Warning) ||
            game.Confidence is DetectionConfidence.Medium)
            return GameReadinessState.ReadyWithWarnings;

        return GameReadinessState.Ready;
    }

    private static string BuildSummary(
        InstalledGame game,
        GameReadinessState state,
        IReadOnlyList<GameReadinessIssue> issues)
    {
        var blocker = issues.FirstOrDefault(issue => issue.Severity == ReadinessIssueSeverity.Blocking);
        return state switch
        {
            GameReadinessState.Ready => $"{game.Name} can be configured safely.",
            GameReadinessState.ReadyWithWarnings => $"{game.Name} can be configured, with warnings to review.",
            GameReadinessState.NeedsConfiguration => blocker?.Message ?? $"{game.Name} needs configuration before a safe deployment.",
            GameReadinessState.NeedsUserSelection => blocker?.Message ?? $"{game.Name} needs a user selection before deployment.",
            GameReadinessState.Unsupported => blocker?.Message ?? $"{game.Name} is unsupported for automatic deployment.",
            GameReadinessState.Unavailable => blocker?.Message ?? $"{game.Name} is temporarily unavailable.",
            _ => blocker?.Message ?? $"Readiness for {game.Name} could not be determined."
        };
    }

    private static string BuildNextAction(
        GameReadinessState state,
        RecommendedSetupResult setup,
        IReadOnlyList<GameReadinessIssue> issues,
        LaunchConfigurationResult? launch)
    {
        if (issues.Any(issue => issue.Code is ReadinessIssueCodes.InterruptedDeployment or ReadinessIssueCodes.RecoveryRequired))
            return "Recover interrupted operation";
        if (issues.Any(issue => issue.Code is ReadinessIssueCodes.ExecutableMissing or ReadinessIssueCodes.ExecutableAmbiguous or ReadinessIssueCodes.ManualOverrideInvalid))
            return "Choose executable";
        if (issues.Any(issue => issue.Code is ReadinessIssueCodes.DeploymentDirectoryMissing or ReadinessIssueCodes.DeploymentDirectoryUnsafe))
            return "Choose deployment directory";
        if (launch is { ManualCopyRequired: true } && state is GameReadinessState.NeedsConfiguration or GameReadinessState.ReadyWithWarnings)
            return "Copy launch configuration";
        if (setup.PrimaryOptionId is not null &&
            setup.Options.FirstOrDefault(option => option.Id == setup.PrimaryOptionId) is { IsSupported: true })
            return "Review and apply recommended setup";
        return state switch
        {
            GameReadinessState.Ready => "Review recommended setup",
            GameReadinessState.Unsupported => "Open diagnostics",
            GameReadinessState.Unavailable => "Refresh readiness",
            _ => "Review issues"
        };
    }

    private static string BuildFingerprint(InstalledGame game, GameReadinessEvaluationOptions options)
    {
        var builder = new StringBuilder();
        builder.Append(RulesVersion).Append('|')
            .Append(RecommendedSetupService.RulesVersion).Append('|')
            .Append(game.EffectiveInstallId).Append('|')
            .Append(game.Executable ?? "").Append('|')
            .Append(game.DeploymentDirectory ?? "").Append('|')
            .Append(game.Prefix ?? "").Append('|')
            .Append(game.Confidence).Append('|')
            .Append(game.IsStale).Append('|')
            .Append(game.RequiresConfirmation).Append('|')
            .Append(options.WarnBeforeAntiCheatDeployments).Append('|')
            .Append(options.PreferExistingManagedVersions).Append('|')
            .Append(options.ArtifactMetadataVersion ?? "").Append('|')
            .Append(options.ManualOverride?.Executable ?? "").Append('|')
            .Append(options.ManualOverride?.DeploymentDirectory ?? "").Append('|');
        if (game.Fingerprint is { } fingerprint)
            builder.Append(fingerprint.CanonicalRoot).Append('|').Append(fingerprint.ScannedAt.UtcTicks)
                .Append('|').Append(fingerprint.FilesVisited);
        var recovery = DeploymentRecoveryProbe.Probe(game.GameRoot);
        builder.Append('|').Append(recovery.HasInterruptedTransaction).Append('|').Append(recovery.HasManagedBackups);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Dictionary<string, string> Context(InstalledGame game, params (string Key, string Value)[] extra)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["installId"] = game.EffectiveInstallId,
            ["launcher"] = game.Launcher.ToString(),
            ["store"] = game.Store.ToString()
        };
        foreach (var (key, value) in extra)
            map[key] = value;
        return map;
    }
}
