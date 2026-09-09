using System.ComponentModel;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Gui;

public sealed class DiagnosticItemViewModel(SourceDiagnostic diagnostic)
{
    public string Severity { get; } = diagnostic.Severity.ToString();
    public string Provider { get; } = string.IsNullOrWhiteSpace(diagnostic.ProviderId) ? "unknown" : diagnostic.ProviderId;
    public string Code { get; } = diagnostic.Code;
    public string Summary { get; } = diagnostic.Message;
    public string Path { get; } = diagnostic.MetadataPath ?? string.Empty;
    public string Detail { get; } = diagnostic.TechnicalDetail ?? string.Empty;
    public bool HasPath => Path.Length > 0;
    public bool HasDetail => Detail.Length > 0;
    public bool IsError => diagnostic.Severity == SourceDiagnosticSeverity.Error;
    public bool IsWarning => diagnostic.Severity == SourceDiagnosticSeverity.Warning;
    public bool IsInfo => diagnostic.Severity == SourceDiagnosticSeverity.Info;
}

public sealed class ProviderStatusRowViewModel(
    string id,
    string name,
    bool isEnabled,
    bool isDetected,
    int rootCount,
    int gameCount,
    string status,
    string rootPath)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool IsEnabled { get; } = isEnabled;
    public bool IsDetected { get; } = isDetected;
    public int RootCount { get; } = rootCount;
    public int GameCount { get; } = gameCount;
    public string Status { get; } = status;
    public string RootPath { get; } = rootPath;
    public string DetectionLabel => IsDetected ? "Detected" : "Not detected";
    public string EnabledLabel => IsEnabled ? "Enabled" : "Disabled";
    public bool HasRootPath => RootPath.Length > 0;
}

public sealed class ComponentCardViewModel : INotifyPropertyChanged
{
    private readonly Action<ComponentCardViewModel>? expanded;
    private readonly IReadOnlyList<string> statusFiles;
    private bool isDetailsExpanded;

    public ComponentCardViewModel(
        ComponentStatus status,
        ResolvedArtifact? resolved = null,
        Action<ComponentCardViewModel>? expanded = null,
        Uri? officialPageUrl = null,
        IReadOnlyList<string>? compatibilityNotes = null)
    {
        this.expanded = expanded;
        statusFiles = status.Files;
        Component = status.Component;
        Name = status.Component switch
        {
            ComponentKind.RenoDx => "RenoDX",
            ComponentKind.OptiScaler => "OptiScaler",
            _ => "ReShade"
        };
        Health = status.Health;
        var fromDiscussion = resolved?.ReleaseTag?.StartsWith("discussion-", StringComparison.OrdinalIgnoreCase) == true ||
            resolved?.OfficialSource?.AbsolutePath.Contains("/discussions/", StringComparison.OrdinalIgnoreCase) == true;
        var fromSnapshot = !fromDiscussion &&
            resolved?.GameProfile?.StartsWith("snapshot:", StringComparison.OrdinalIgnoreCase) == true;
        State = status.Lifecycle is ComponentLifecycleState.Unknown or ComponentLifecycleState.Checking
            ? status.Health switch
            {
                ComponentHealth.Installed when status.Verification == InstallationVerification.MetadataUnverified =>
                    "Installed, metadata incomplete",
                ComponentHealth.Installed => "Installed",
                ComponentHealth.Outdated => "Update available",
                ComponentHealth.Broken or ComponentHealth.PartiallyInstalled => "Repair needed",
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable when
                    status.Verification == InstallationVerification.RepairNeeded => "Repair needed",
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable => "Needs attention",
                ComponentHealth.Experimental => "Experimental",
                ComponentHealth.Unsupported => "Unsupported",
                ComponentHealth.Unavailable => status.Explanation.Contains("catalog unavailable", StringComparison.OrdinalIgnoreCase)
                    ? "Catalog unavailable"
                    : status.Explanation.Contains("manual download", StringComparison.OrdinalIgnoreCase) ||
                      status.Explanation.Contains("No direct addon download", StringComparison.OrdinalIgnoreCase)
                        ? "Manual download required"
                        : status.Explanation.Contains("No RenoDX addon found", StringComparison.OrdinalIgnoreCase) ||
                          status.Explanation.Contains("Not listed", StringComparison.OrdinalIgnoreCase)
                            ? "Not listed"
                            : "Unavailable",
                ComponentHealth.Supported when status.Explanation.Contains("No direct addon download", StringComparison.OrdinalIgnoreCase) =>
                    "Official page available",
                ComponentHealth.Supported when status.Explanation.Contains("manual download", StringComparison.OrdinalIgnoreCase) =>
                    "Manual download required",
                ComponentHealth.Supported when status.Explanation.Contains("another executable", StringComparison.OrdinalIgnoreCase) =>
                    "Addon available for another executable",
                ComponentHealth.Conflicting or ComponentHealth.MissingDependency => "Blocked",
                ComponentHealth.ForeignInstallation or ComponentHealth.ManifestUnavailable => "Unknown existing installation",
                ComponentHealth.DownloadRequired or ComponentHealth.Cached when status.Component == ComponentKind.RenoDx &&
                    (fromSnapshot || fromDiscussion ||
                     resolved?.Support is ArtifactSupportKind.ExactGameProfile or ArtifactSupportKind.ExecutableOrAliasProfile) =>
                    "Exact addon available",
                _ => "Not installed"
            }
            : status.Lifecycle switch
            {
                ComponentLifecycleState.InstalledMetadataIncomplete => "Installed, metadata incomplete",
                ComponentLifecycleState.InstalledWithWarnings => "Installed with warnings",
                ComponentLifecycleState.InstalledUnmanaged => "Installed (unmanaged)",
                ComponentLifecycleState.InstalledHealthy => "Installed",
                ComponentLifecycleState.UpdateAvailable => "Update available",
                ComponentLifecycleState.RepairRequired => "Repair needed",
                ComponentLifecycleState.RepairRecommended => "Repair recommended",
                ComponentLifecycleState.Conflict => "Blocked",
                ComponentLifecycleState.Unsupported => "Unsupported",
                ComponentLifecycleState.Unknown => "Unknown existing installation",
                ComponentLifecycleState.NotInstalled when status.Health == ComponentHealth.Experimental => "Experimental",
                ComponentLifecycleState.NotInstalled when status.Health == ComponentHealth.Unavailable =>
                    status.Explanation.Contains("catalog unavailable", StringComparison.OrdinalIgnoreCase)
                        ? "Catalog unavailable"
                        : status.Explanation.Contains("manual download", StringComparison.OrdinalIgnoreCase) ||
                          status.Explanation.Contains("No direct addon download", StringComparison.OrdinalIgnoreCase)
                            ? "Manual download required"
                            : status.Explanation.Contains("No RenoDX addon found", StringComparison.OrdinalIgnoreCase) ||
                              status.Explanation.Contains("Not listed", StringComparison.OrdinalIgnoreCase)
                                ? "Not listed"
                                : "Unavailable",
                ComponentLifecycleState.NotInstalled when status.Health == ComponentHealth.Supported &&
                    status.Explanation.Contains("No direct addon download", StringComparison.OrdinalIgnoreCase) =>
                    "Official page available",
                ComponentLifecycleState.NotInstalled when status.Health == ComponentHealth.Supported &&
                    status.Explanation.Contains("manual download", StringComparison.OrdinalIgnoreCase) =>
                    "Manual download required",
                ComponentLifecycleState.NotInstalled when status.Health == ComponentHealth.Supported &&
                    status.Explanation.Contains("another executable", StringComparison.OrdinalIgnoreCase) =>
                    "Addon available for another executable",
                ComponentLifecycleState.NotInstalled when status.Health is ComponentHealth.Conflicting or ComponentHealth.MissingDependency =>
                    "Blocked",
                ComponentLifecycleState.NotInstalled when status.Health is (ComponentHealth.DownloadRequired or ComponentHealth.Cached) &&
                    status.Component == ComponentKind.RenoDx &&
                    (fromSnapshot || fromDiscussion ||
                     resolved?.Support is ArtifactSupportKind.ExactGameProfile or ArtifactSupportKind.ExecutableOrAliasProfile) =>
                    "Exact addon available",
                _ => "Not installed"
            };
        Version = status.Version;
        InstalledVersion = status.Version ?? "Not identified";
        AvailableVersion = resolved?.Version ?? resolved?.ReleaseTag ?? "Not checked";
        Ownership = status.Ownership switch
        {
            OwnershipHealth.Managed or OwnershipHealth.Migrated => "Managed by RHI",
            OwnershipHealth.Foreign or OwnershipHealth.Unmanaged => "Detected existing installation",
            OwnershipHealth.Incomplete => "Partially managed",
            _ => "Ownership unknown"
        };
        TechnicalExplanation = BuildTechnicalExplanation(status);
        Explanation = status.RepairReason is { Length: > 0 } repairReason
            ? repairReason
            : status.Health switch
            {
                ComponentHealth.Installed when status.Verification == InstallationVerification.MetadataUnverified =>
                    "Installed files are ready. Ownership metadata differs from disk and does not require repair.",
                ComponentHealth.Installed => "Installed files are ready to use.",
                ComponentHealth.Outdated => "A newer compatible version is available.",
                ComponentHealth.Broken or ComponentHealth.PartiallyInstalled =>
                    status.Explanation,
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable when
                    status.Verification == InstallationVerification.RepairNeeded =>
                    status.Explanation,
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable =>
                    "Installed files are present, but configuration differs from the recommended layout.",
                ComponentHealth.Unsupported => "RenoDX cannot safely apply to this game.",
                ComponentHealth.Unavailable => status.Explanation,
                ComponentHealth.Supported => status.Explanation,
                ComponentHealth.ForeignInstallation or ComponentHealth.ManifestUnavailable => "Existing files will be left unchanged.",
                ComponentHealth.Conflicting or ComponentHealth.MissingDependency => "No safe automatic method is currently available.",
                ComponentHealth.DownloadRequired or ComponentHealth.Cached when status.Component == ComponentKind.RenoDx &&
                    resolved?.Support is ArtifactSupportKind.ExactGameProfile or ArtifactSupportKind.ExecutableOrAliasProfile =>
                    status.Explanation,
                _ => "This component is not installed."
            };
        Files = status.Files.Count == 0 ? "No managed files" : string.Join(", ", status.Files);
        SourceProfile = fromSnapshot ? "Official RenoDX snapshot release" :
            fromDiscussion ? "Official RenoDX Discussion" : resolved?.Support switch
            {
                ArtifactSupportKind.ExactGameProfile => "Exact game profile",
                ArtifactSupportKind.ExecutableOrAliasProfile => "Known game profile",
                ArtifactSupportKind.UnityFallback => "Unity fallback",
                ArtifactSupportKind.UnrealFallback => "Unreal fallback",
                ArtifactSupportKind.General => "Official release",
                _ when officialPageUrl is not null && status.Component == ComponentKind.RenoDx => "Official RenoDX page",
                _ => "No compatible source"
            };
        CachePath = resolved?.Selection?.CachedPath ?? string.Empty;
        Hash = resolved?.Sha256 ?? string.Empty;
        OfficialPageUrl = officialPageUrl;
        CanOpenOfficialPage = status.Component == ComponentKind.RenoDx && officialPageUrl is not null;
        OfficialPageActionText = officialPageUrl?.Host.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase) == true
            ? "Open Nexus Mods"
            : officialPageUrl?.Host.Contains("discord", StringComparison.OrdinalIgnoreCase) == true
                ? "Open RenoDX Discord"
                : "Open official page";
        CompatibilityNote = compatibilityNotes is { Count: > 0 }
            ? string.Join('\n', compatibilityNotes)
            : string.Empty;
        var foreign = status.Ownership == OwnershipHealth.Foreign ||
            status.Verification == InstallationVerification.RecognizedExisting ||
            status.Health == ComponentHealth.ForeignInstallation ||
            status.Lifecycle == ComponentLifecycleState.InstalledUnmanaged ||
            status.Update == UpdateAvailability.ManualInstallationDetected ||
            status.Explanation.Contains("not owned", StringComparison.OrdinalIgnoreCase) ||
            status.Explanation.Contains("Installed manually", StringComparison.OrdinalIgnoreCase);
        var manualUpToDate = foreign && status.Update == UpdateAvailability.UpToDate;
        var versionUnknown = status.Update == UpdateAvailability.InstalledVersionUnknown &&
            status.Lifecycle is ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings or
                ComponentLifecycleState.InstalledMetadataIncomplete or ComponentLifecycleState.InstalledUnmanaged;
        var automaticInstallAvailable = resolved?.Selection is not null ||
            status.Lifecycle == ComponentLifecycleState.NotInstalled &&
            status.Health is (ComponentHealth.Available or ComponentHealth.DownloadRequired or
                ComponentHealth.Cached or ComponentHealth.Experimental) &&
            (status.Component != ComponentKind.RenoDx ||
             status.Explanation.Contains("automatically", StringComparison.OrdinalIgnoreCase));
        CanInstall = !foreign && automaticInstallAvailable &&
            status.Health is (ComponentHealth.Available or ComponentHealth.Supported or ComponentHealth.DownloadRequired or
                ComponentHealth.Cached or ComponentHealth.Experimental) &&
            status.Lifecycle is (ComponentLifecycleState.NotInstalled or ComponentLifecycleState.Unknown or
                ComponentLifecycleState.Checking) &&
            !(status.Component == ComponentKind.RenoDx &&
              (status.Explanation.Contains("manual download", StringComparison.OrdinalIgnoreCase) ||
               status.Explanation.Contains("No direct addon download", StringComparison.OrdinalIgnoreCase)));
        CanUpdate = !foreign &&
            status.Update is not (UpdateAvailability.ManualInstallationDetected or
                UpdateAvailability.InstalledVersionUnknown or UpdateAvailability.StatusUnavailable or
                UpdateAvailability.UpToDate) &&
            (status.Update == UpdateAvailability.UpdateAvailable ||
             status.Lifecycle == ComponentLifecycleState.UpdateAvailable ||
             status.Health == ComponentHealth.Outdated);
        CanRepair = !foreign && (
            status.Lifecycle is ComponentLifecycleState.RepairRequired or ComponentLifecycleState.RepairRecommended ||
            status.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled ||
            (status.Health is ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable &&
             status.Verification == InstallationVerification.RepairNeeded));
        var canRemoveRecognizedFiles = statusFiles.Count > 0 &&
            status.Lifecycle == ComponentLifecycleState.InstalledUnmanaged &&
            status.Verification == InstallationVerification.RecognizedExisting;
        CanRemove = canRemoveRecognizedFiles || !foreign && (
            status.Lifecycle is ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings or
                ComponentLifecycleState.InstalledMetadataIncomplete or ComponentLifecycleState.UpdateAvailable or
                ComponentLifecycleState.RepairRequired or ComponentLifecycleState.RepairRecommended ||
            status.Health is ComponentHealth.Installed or ComponentHealth.Outdated or ComponentHealth.Broken or
                ComponentHealth.PartiallyInstalled or ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable);
        CanShowFiles = foreign && statusFiles.Count > 0 &&
            status.Health is not (ComponentHealth.ForeignInstallation or ComponentHealth.ManifestUnavailable);
        CanCheckAgain = (foreign && status.Health is not (ComponentHealth.ForeignInstallation or ComponentHealth.ManifestUnavailable)) ||
            versionUnknown;
        NoActionNeeded = ((foreign && status.Lifecycle == ComponentLifecycleState.InstalledUnmanaged) || versionUnknown) &&
            !CanInstall && !CanUpdate && !CanRepair && !CanRemove;
        CanUseDownloadedArtifact = status.Component == ComponentKind.RenoDx && officialPageUrl is not null &&
            resolved?.Selection is null && !foreign;
        if (manualUpToDate)
        {
            State = "Installed";
            Explanation = "Up to date";
        }
        else if (canRemoveRecognizedFiles)
        {
            State = "Installed manually";
            Explanation = "Recognized component files can be removed safely after reviewing the recovery plan.";
        }
        else if (versionUnknown)
        {
            State = "Installed";
            Explanation = "Version not identified";
        }
        DisabledReason = foreign && !NoActionNeeded && !canRemoveRecognizedFiles
            ? "Detected files are not owned by RHI Linux and cannot be changed safely."
            : foreign
                ? string.Empty
                : status.Health switch
                {
                    ComponentHealth.Conflicting => status.Explanation,
                    ComponentHealth.MissingDependency => status.Explanation,
                    ComponentHealth.ManifestUnavailable => status.Explanation,
                    ComponentHealth.Unsupported => status.Explanation,
                    _ => string.Empty
                };
        HasDisabledReason = DisabledReason.Length > 0;
        ActionText = CanRepair ? "Repair" : CanUpdate ? "Update" : CanInstall ? "Install" :
            CanRemove && !CanRepair && !CanUpdate && !CanInstall ? "Remove" :
            NoActionNeeded ? "No action needed" : string.Empty;
        ShowRemoveAction = CanRemove && (CanRepair || CanUpdate || CanInstall);
        ShowSecondaryAction = CanShowFiles || CanCheckAgain;
        SecondaryActionText = CanShowFiles ? "Show files" : CanCheckAgain ? "Check again" : string.Empty;
        RemoveActionText = Component switch
        {
            ComponentKind.RenoDx => "Remove RenoDX",
            ComponentKind.OptiScaler => "Remove OptiScaler",
            _ => "Remove ReShade"
        };
        Lifecycle = status.Lifecycle;
        RepairReason = status.RepairReason;
        ReasonCode = status.ReasonCode;
        UpdateAvailability = status.Update;
    }

    private static string BuildTechnicalExplanation(ComponentStatus status)
    {
        if (status.Lifecycle is (ComponentLifecycleState.Unknown or ComponentLifecycleState.Checking) &&
            status.ReasonCode is null && status.RepairReason is null)
            return status.Diagnostic ?? status.Explanation;

        var parts = new List<string>();
        if (status.Diagnostic is { Length: > 0 }) parts.Add(status.Diagnostic);
        else if (status.Explanation is { Length: > 0 }) parts.Add(status.Explanation);
        if (status.RepairReason is { Length: > 0 }) parts.Add($"Repair reason: {status.RepairReason}");
        if (status.ReasonCode is { Length: > 0 }) parts.Add($"Reason code: {status.ReasonCode}");
        if (status.Lifecycle is not (ComponentLifecycleState.Unknown or ComponentLifecycleState.Checking))
        {
            parts.Add($"Lifecycle: {status.Lifecycle}");
            parts.Add($"Runtime: {status.Runtime}");
            parts.Add($"Ownership: {status.Ownership}");
            parts.Add($"Update: {status.Update}");
        }
        return parts.Count == 0 ? status.Explanation : string.Join('\n', parts);
    }

    public ComponentKind Component { get; }
    public string Name { get; }
    public ComponentHealth Health { get; }
    public string State { get; private set; }
    public string? Version { get; }
    public string InstalledVersion { get; }
    public string AvailableVersion { get; }
    public string Ownership { get; }
    public string Explanation { get; private set; }
    public string TechnicalExplanation { get; }
    public string SourceProfile { get; }
    public string Files { get; }
    public string CachePath { get; }
    public string Hash { get; }
    public Uri? OfficialPageUrl { get; }
    public bool CanOpenOfficialPage { get; }
    public string OfficialPageActionText { get; }
    public bool CanUseDownloadedArtifact { get; }
    public string CompatibilityNote { get; }
    public bool HasCompatibilityNote => CompatibilityNote.Length > 0;
    public bool CanInstall { get; }
    public bool CanUpdate { get; }
    public bool CanRemove { get; }
    public bool CanRepair { get; }
    public bool CanShowFiles { get; }
    public bool CanCheckAgain { get; }
    public bool NoActionNeeded { get; }
    public bool ShowRemoveAction { get; }
    public bool ShowSecondaryAction { get; }
    public bool CanAct => ActionText.Length > 0 && ActionText != "No action needed";
    public string ActionText { get; }
    public string SecondaryActionText { get; }
    public string RemoveActionText { get; }
    public ComponentLifecycleState Lifecycle { get; }
    public UpdateAvailability UpdateAvailability { get; }
    public string? RepairReason { get; }
    public string? ReasonCode { get; }
    public bool HasDisabledReason { get; }
    public string DisabledReason { get; }
    public bool HasVersion => !string.IsNullOrWhiteSpace(Version);
    public bool HasCachePath => CachePath.Length > 0;
    public bool HasHash => Hash.Length > 0;
    public string MaterialSignature => $"{Health}|{Lifecycle}|{Version}|{Explanation}|{string.Join('|', statusFiles)}|{SourceProfile}|{Hash}";
    public bool IsDetailsExpanded
    {
        get => isDetailsExpanded;
        set
        {
            if (isDetailsExpanded == value) return;
            isDetailsExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDetailsExpanded)));
            if (value) expanded?.Invoke(this);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ExecutableCandidateDisplay(
    string FileName,
    string RelativePath,
    string Architecture,
    string Confidence,
    int Score,
    string Reason,
    string Path,
    bool IsCurrent,
    bool IsRejected);

public sealed record GameSelectionSnapshot(
    InstalledGame Game,
    long Generation,
    bool IsLoading,
    IReadOnlyList<ComponentCardViewModel> ComponentCards,
    string ProfileSummary,
    string SelectedProxy,
    string DependencySummary,
    string SupportTitle,
    string SupportMessage,
    bool CanInstallRecommendedStack,
    UpdateCheckState UpdateState,
    bool IsCheckingUpdates,
    bool HasUpdates,
    OptiScalerCompatibilityLevel? OptiScalerLevel,
    PrimaryActionKind PrimaryAction,
    string Progress,
    ExecutionResult? PreviousOperationResult,
    string RequiredLaunchOption = "",
    SelectionPhase Phase = SelectionPhase.Idle,
    string TimingDiagnostics = "")
{
    public static GameSelectionSnapshot Loading(InstalledGame game, long generation, bool canCheckUpdates) => new(
        game,
        generation,
        true,
        [],
        "Detecting game…",
        "Looking for a safe compatibility filename…",
        "Reading installed component state…",
        "Detecting game",
        "Reading local game files and compatibility options.",
        false,
        canCheckUpdates ? UpdateCheckState.Checking : UpdateCheckState.UnableToCheck,
        false,
        false,
        null,
        PrimaryActionKind.None,
        string.Empty,
        null,
        string.Empty,
        SelectionPhase.DetectingGame,
        string.Empty);
}
