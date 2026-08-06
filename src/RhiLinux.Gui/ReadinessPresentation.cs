using System.ComponentModel;
using System.Runtime.CompilerServices;
using RhiLinux.Core;

namespace RhiLinux.Gui;

public sealed class ReadinessIssueItemViewModel(GameReadinessIssue issue)
{
    public string Code { get; } = issue.Code;
    public string Severity { get; } = issue.Severity.ToString();
    public string Title { get; } = issue.Title;
    public string Message { get; } = issue.Message;
    public string? ActionId { get; } = issue.ActionId;
    public bool IsBlocking { get; } = issue.Severity == ReadinessIssueSeverity.Blocking;
    public bool IsWarning { get; } = issue.Severity == ReadinessIssueSeverity.Warning;
    public bool HasAction { get; } = !string.IsNullOrWhiteSpace(issue.ActionId);
    public string ActionLabel => issue.ActionId switch
    {
        "choose-executable" => "Choose executable",
        "choose-deployment-directory" => "Choose deployment directory",
        "copy-launch-configuration" => "Copy launch configuration",
        "review-recommended-setup" => "Review recommended setup",
        "review-updates" => "Review updates",
        "recover-interrupted" => "Recover interrupted operation",
        "restore-backups" => "Restore previous files",
        "open-diagnostics" => "Open diagnostics",
        "refresh-readiness" => "Refresh readiness",
        "refresh-component-information" => "Refresh component information",
        _ => "Open"
    };
}

public sealed class RecommendedSetupOptionViewModel(RecommendedSetupOption option)
{
    public string Id { get; } = option.Id;
    public string Title { get; } = option.Title;
    public string Explanation { get; } = option.Explanation;
    public bool IsSupported { get; } = option.IsSupported;
    public bool IsPrimary { get; } = option.IsPrimary;
    public string? UnsupportedReason { get; } = option.UnsupportedReason;
    public bool HasUnsupportedReason => !string.IsNullOrWhiteSpace(UnsupportedReason);
    public double SupportOpacity => IsSupported ? 1 : 0.72;
}

public sealed class ComponentUpdateItemViewModel(
    InstalledGame game,
    ComponentStatus status,
    GameReadinessState readinessImpact)
{
    public string GameName { get; } = game.Name;
    public string InstallId { get; } = game.EffectiveInstallId;
    public string ComponentName { get; } = status.Component switch
    {
        ComponentKind.RenoDx => "RenoDX",
        ComponentKind.OptiScaler => "OptiScaler",
        _ => "ReShade"
    };
    public string InstalledVersion { get; } = status.Version ?? "Unknown";
    public string AvailableVersion { get; } = status.Health == ComponentHealth.Outdated
        ? "Newer official release"
        : status.Version ?? "Unknown";
    public string UpdateState { get; } = status.Health switch
    {
        ComponentHealth.Outdated => "Update available",
        ComponentHealth.Installed => "Up to date",
        ComponentHealth.Unavailable => "Not installed",
        ComponentHealth.Unsupported => "Unsupported",
        _ => "Unknown"
    };
    public string ReadinessImpact { get; } = readinessImpact.ToString();
    public bool CanReview { get; } = status.Health == ComponentHealth.Outdated;
}

public sealed class ReadinessPresentation : INotifyPropertyChanged
{
    private GameReadinessResult? result;
    private bool isLoading;
    private string? lastCopied;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameReadinessResult? Result
    {
        get => result;
        private set
        {
            result = value;
            OnPropertyChanged();
            RaiseDerived();
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        set
        {
            if (isLoading == value) return;
            isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowLoading));
        }
    }

    public bool ShowLoading => IsLoading && Result is null;
    public bool HasResult => Result is not null;
    public string StateLabel => Result?.State.ToString() ?? "Evaluating";
    public string Summary => Result?.Summary ?? "Evaluating readiness…";
    public string NextAction => Result?.NextAction ?? "Waiting for evaluation";
    public int BlockingCount => Result?.BlockingIssueCount ?? 0;
    public int WarningCount => Result?.WarningIssueCount ?? 0;
    public string IssueCountsText =>
        BlockingCount == 0 && WarningCount == 0
            ? "No blockers or warnings"
            : $"{BlockingCount} blockers · {WarningCount} warnings";
    public IReadOnlyList<ReadinessIssueItemViewModel> Issues =>
        Result?.Issues.Select(issue => new ReadinessIssueItemViewModel(issue)).ToArray() ?? [];
    public IReadOnlyList<RecommendedSetupOptionViewModel> SetupOptions =>
        Result?.RecommendedSetup?.Options.Select(option => new RecommendedSetupOptionViewModel(option)).ToArray()
        ?? [];
    public string SetupSummary => Result?.RecommendedSetup?.Summary ?? string.Empty;
    public bool HasSetupOptions => SetupOptions.Count > 0;
    public bool HasIssues => Issues.Count > 0;
    public string? LaunchValue => Result?.LaunchConfiguration?.DisplayValue;
    public string? LaunchCopyValue => Result?.LaunchConfiguration?.CopyValue;
    public string LaunchInstructions => Result?.LaunchConfiguration?.PlacementInstructions ?? string.Empty;
    public string LaunchExplanation => Result?.LaunchConfiguration?.Explanation ?? string.Empty;
    public bool HasLaunchConfiguration => !string.IsNullOrWhiteSpace(LaunchValue);
    public bool CanCopyLaunchConfiguration => !string.IsNullOrWhiteSpace(LaunchCopyValue);
    public IReadOnlyList<string> PlanHighlights => Result?.RecommendedPlan?.Highlights ?? [];
    public bool HasPlanSummary => PlanHighlights.Count > 0;
    public string LastCopiedFeedback
    {
        get => lastCopied ?? string.Empty;
        private set
        {
            lastCopied = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCopiedFeedback));
        }
    }
    public bool ShowCopiedFeedback => !string.IsNullOrWhiteSpace(LastCopiedFeedback);
    public bool ShowReadyBadge => Result?.State is GameReadinessState.Ready or GameReadinessState.ReadyWithWarnings;
    public bool ShowNeedsAttentionBadge => Result?.State is GameReadinessState.NeedsConfiguration
        or GameReadinessState.NeedsUserSelection or GameReadinessState.Error;
    public bool ShowUnsupportedBadge => Result?.State is GameReadinessState.Unsupported or GameReadinessState.Unavailable;
    public string ExecutableDisplay => Result?.Target?.Executable is { Length: > 0 } executable
        ? Path.GetFileName(executable)
        : "Not detected";
    public string ExecutablePath => Result?.Target?.Executable ?? string.Empty;
    public string DeploymentDirectoryDisplay => Result?.Target is { DeploymentDirectory.Length: > 0 } target
        ? target.DeploymentDirectory
        : "Not detected";
    public string PrefixDisplay => Result?.Target is { HasProtonPrefix: true, ProtonPrefix.Length: > 0 } target
        ? target.ProtonPrefix
        : "Not available";
    public string LauncherDisplay => Result?.Target is null
        ? string.Empty
        : string.Join(" · ", new[]
        {
            Result.Target.Launcher.ToString(),
            Result.Target.Store.ToString(),
            Result.Target.Environment.ToString()
        }.Distinct(StringComparer.OrdinalIgnoreCase));

    public void SetResult(GameReadinessResult? value)
    {
        Result = value;
        IsLoading = false;
        LastCopiedFeedback = string.Empty;
    }

    public void MarkCopied() => LastCopiedFeedback = "Copied to clipboard";

    public void AttachPlanSummary(DeploymentPlanSummary summary)
    {
        if (Result is null) return;
        Result = Result with { RecommendedPlan = summary };
    }

    private void RaiseDerived()
    {
        foreach (var name in new[]
                 {
                     nameof(HasResult), nameof(StateLabel), nameof(Summary), nameof(NextAction),
                     nameof(BlockingCount), nameof(WarningCount), nameof(IssueCountsText), nameof(Issues),
                     nameof(SetupOptions), nameof(SetupSummary), nameof(HasSetupOptions), nameof(HasIssues),
                     nameof(LaunchValue), nameof(LaunchCopyValue), nameof(LaunchInstructions),
                     nameof(LaunchExplanation), nameof(HasLaunchConfiguration), nameof(CanCopyLaunchConfiguration),
                     nameof(PlanHighlights), nameof(HasPlanSummary), nameof(ShowReadyBadge),
                     nameof(ShowNeedsAttentionBadge), nameof(ShowUnsupportedBadge), nameof(ExecutableDisplay),
                     nameof(ExecutablePath), nameof(DeploymentDirectoryDisplay), nameof(PrefixDisplay),
                     nameof(ShowLoading)
                 })
            OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
