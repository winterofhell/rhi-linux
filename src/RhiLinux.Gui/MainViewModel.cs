using System.ComponentModel;
using System.Runtime.CompilerServices;
using RhiLinux.Core;
using RhiLinux.Mods;
using RhiLinux.Steam;

namespace RhiLinux.Gui;

public enum UiState
{
    Empty,
    Loading,
    Scanning,
    Ready,
    NoGames,
    Unsupported,
    Conflict,
    Planning,
    Deploying,
    Success,
    Error
}

public enum UpdateCheckState { Checking, UpToDate, UpdateAvailable, Offline, UnableToCheck }

public enum SelectionPhase { Idle, DetectingGame, CheckingCompatibility, CheckingUpdates }

public enum PrimaryActionKind { None, Install, Update, Repair, Remove }

public sealed class ComponentCardViewModel : INotifyPropertyChanged
{
    private readonly Action<ComponentCardViewModel>? expanded;
    private readonly IReadOnlyList<string> statusFiles;
    private bool isDetailsExpanded;

    public ComponentCardViewModel(ComponentStatus status, ResolvedArtifact? resolved = null, Action<ComponentCardViewModel>? expanded = null)
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
        State = status.Health switch
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
            ComponentHealth.Unsupported or ComponentHealth.Unavailable => "Unsupported",
            ComponentHealth.Conflicting or ComponentHealth.MissingDependency => "Blocked",
            ComponentHealth.ForeignInstallation or ComponentHealth.ManifestUnavailable => "Unknown existing installation",
            _ => "Not installed"
        };
        Version = status.Version;
        TechnicalExplanation = status.Diagnostic ?? status.Explanation;
        Explanation = status.Health switch
        {
            ComponentHealth.Installed when status.Verification == InstallationVerification.MetadataUnverified =>
                "Installed files are ready. Ownership metadata differs from disk and does not require repair.",
            ComponentHealth.Installed => "Installed files are ready to use.",
            ComponentHealth.Outdated => "A newer compatible version is available.",
            ComponentHealth.Broken or ComponentHealth.PartiallyInstalled =>
                "Required runtime files from an earlier installation need repair.",
            ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable when
                status.Verification == InstallationVerification.RepairNeeded =>
                "Some files from an earlier installation need repair.",
            ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable =>
                "Installed files are present, but configuration differs from the recommended layout.",
            ComponentHealth.Unsupported or ComponentHealth.Unavailable => "Automatic setup is not available for this component.",
            ComponentHealth.ForeignInstallation or ComponentHealth.ManifestUnavailable => "Existing files will be left unchanged.",
            ComponentHealth.Conflicting or ComponentHealth.MissingDependency => "No safe automatic method is currently available.",
            _ => "This component is not installed."
        };
        Files = status.Files.Count == 0 ? "No managed files" : string.Join(", ", status.Files);
        SourceProfile = resolved?.Support switch
        {
            ArtifactSupportKind.ExactGameProfile => "Exact game profile",
            ArtifactSupportKind.ExecutableOrAliasProfile => "Known game profile",
            ArtifactSupportKind.UnityFallback => "Unity fallback",
            ArtifactSupportKind.UnrealFallback => "Unreal fallback",
            ArtifactSupportKind.General => "Official release",
            _ => "No compatible source"
        };
        CachePath = resolved?.Selection?.CachedPath ?? string.Empty;
        Hash = resolved?.Sha256 ?? string.Empty;
        var foreign = status.Verification == InstallationVerification.RecognizedExisting ||
            status.Health == ComponentHealth.ForeignInstallation ||
            status.Explanation.Contains("not owned", StringComparison.OrdinalIgnoreCase);
        CanInstall = resolved?.Selection is not null &&
            status.Health is (ComponentHealth.Available or ComponentHealth.Supported or ComponentHealth.DownloadRequired or
                ComponentHealth.Cached or ComponentHealth.Experimental);
        CanUpdate = !foreign && status.Health == ComponentHealth.Outdated;
        CanRemove = !foreign && status.Health == ComponentHealth.Installed;
        CanRepair = !foreign && (
            status.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled ||
            (status.Health is ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable &&
             status.Verification == InstallationVerification.RepairNeeded));
        DisabledReason = foreign ? "Detected files are not owned by RHI Linux and cannot be changed safely." : status.Health switch
        {
            ComponentHealth.Conflicting => status.Explanation,
            ComponentHealth.MissingDependency => status.Explanation,
            ComponentHealth.ManifestUnavailable => status.Explanation,
            ComponentHealth.Unsupported => status.Explanation,
            _ => string.Empty
        };
        HasDisabledReason = DisabledReason.Length > 0;
        ActionText = CanRepair ? "Repair and continue" : CanUpdate ? "Update" : CanRemove ? "Remove" : CanInstall ? "Install" : string.Empty;
    }

    public ComponentKind Component { get; }
    public string Name { get; }
    public ComponentHealth Health { get; }
    public string State { get; }
    public string? Version { get; }
    public string Explanation { get; }
    public string TechnicalExplanation { get; }
    public string SourceProfile { get; }
    public string Files { get; }
    public string CachePath { get; }
    public string Hash { get; }
    public bool CanInstall { get; }
    public bool CanUpdate { get; }
    public bool CanRemove { get; }
    public bool CanRepair { get; }
    public bool CanAct => ActionText.Length > 0;
    public string ActionText { get; }
    public bool HasDisabledReason { get; }
    public string DisabledReason { get; }
    public bool HasVersion => !string.IsNullOrWhiteSpace(Version);
    public bool HasCachePath => CachePath.Length > 0;
    public bool HasHash => Hash.Length > 0;
    public string MaterialSignature => $"{Health}|{Version}|{Explanation}|{string.Join('|', statusFiles)}|{SourceProfile}|{Hash}";
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

public sealed record ExecutableCandidateDisplay(int Score, string Confidence, string Path, string Reasons);

public sealed record GameSelectionSnapshot(
    SteamGame Game,
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
    LaunchOptionStatus LaunchOptionStatus = LaunchOptionStatus.NotDetected,
    string LaunchOptionExplanation = "",
    string? DetectedLaunchOption = null,
    SelectionPhase Phase = SelectionPhase.Idle,
    string TimingDiagnostics = "")
{
    public static GameSelectionSnapshot Loading(SteamGame game, long generation, bool canCheckUpdates) => new(
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
        LaunchOptionStatus.NotDetected,
        "Checking the required Steam launch option…",
        null,
        SelectionPhase.DetectingGame,
        string.Empty);
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IGameDiscovery discovery;
    private readonly IComponentStatusProvider statusProvider;
    private readonly IStateStore stateStore;
    private readonly IUiPreferencesStore preferencesStore;
    private readonly DeploymentPlanner planner;
    private readonly IDeploymentExecutor executor;
    private readonly IStackStatusProvider? stackStatusProvider;
    private readonly XdgPaths paths;
    private readonly ArtifactCacheService artifactCache;
    private readonly Func<SteamGame, CancellationToken, Task<DeploymentPlan>>? recommendedPlanBuilder;
    private ApplicationState applicationState = new();
    private List<SteamGame> games = [];
    private IReadOnlyList<SteamGame> filteredGames = [];
    private GameSelectionSnapshot? activeSnapshot;
    private string searchText = string.Empty;
    private string globalStatus = "Ready to scan";
    private string? errorMessage;
    private UiState currentState = UiState.Empty;
    private bool isBusy;
    private long selectionGeneration;
    private CancellationTokenSource? selectionCancellation;
    private readonly object selectionLock = new();
    private string cacheSizeText = "Calculating…";
    private string cacheCleanupText = "Never";
    private bool isCacheBusy;
    private long busyGeneration;

    public MainViewModel() : this(null) { }

    public MainViewModel(IReadOnlyList<string>? steamRoots)
    {
        paths = new XdgPaths();
        discovery = new GameDiscoveryAdapter(new SteamDiscoveryService(new ExecutableDetector()), steamRoots);
        var httpClient = MetadataHttp.CreateClient();
        var stackStatusService = new StackStatusService(httpClient, paths);
        stackStatusProvider = new StackReportProviderAdapter(stackStatusService);
        var catalog = new GameProfileCatalog(paths);
        statusProvider = new ComponentStatusProviderAdapter(new ComponentDetector(catalog));
        stateStore = new JsonStateStore(paths.StateFile);
        preferencesStore = new JsonUiPreferencesStore(Path.Combine(paths.AppConfigDirectory, "ui.json"));
        planner = new DeploymentPlanner();
        executor = new DeploymentExecutor();
        artifactCache = new ArtifactCacheService(paths);
    }

    public MainViewModel(
        IGameDiscovery discovery,
        IComponentStatusProvider statusProvider,
        IStateStore stateStore,
        IUiPreferencesStore preferencesStore,
        DeploymentPlanner? planner = null,
        IDeploymentExecutor? executor = null,
        IStackStatusProvider? stackStatusProvider = null,
        Func<SteamGame, CancellationToken, Task<DeploymentPlan>>? recommendedPlanBuilder = null)
    {
        this.discovery = discovery;
        this.statusProvider = statusProvider;
        this.stateStore = stateStore;
        this.preferencesStore = preferencesStore;
        this.planner = planner ?? new DeploymentPlanner();
        this.executor = executor ?? new DeploymentExecutor();
        this.stackStatusProvider = stackStatusProvider;
        this.recommendedPlanBuilder = recommendedPlanBuilder;
        paths = new XdgPaths();
        artifactCache = new ArtifactCacheService(paths);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public UiPreferences Preferences { get; private set; } = new();
    public IReadOnlyList<SteamGame> Games => games;
    public IReadOnlyList<SteamGame> FilteredGames { get => filteredGames; private set => Set(ref filteredGames, value); }
    public SteamGame? SelectedGame => activeSnapshot?.Game;
    public IReadOnlyList<ComponentCardViewModel> ComponentCards => activeSnapshot?.ComponentCards ?? [];
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!Set(ref searchText, value ?? string.Empty)) return;
            Preferences.SearchText = searchText;
            ApplyFilter();
        }
    }
    public string GlobalStatus { get => globalStatus; private set => Set(ref globalStatus, value); }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public UiState CurrentState { get => currentState; private set { if (Set(ref currentState, value)) RaiseStateProperties(); } }
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(CanInteract));
            OnPropertyChanged(nameof(CanInstallRecommendedStack));
            OnPropertyChanged(nameof(CanCheckForUpdates));
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(CanManageCache));
        }
    }
    public bool CanInteract => !IsBusy;
    public bool CanInstallRecommendedStack => activeSnapshot?.CanInstallRecommendedStack == true && CanInteract;
    public bool CanCheckForUpdates => !IsBusy && !IsCheckingUpdates && HasSelection;
    public bool IsCheckingUpdates => activeSnapshot?.IsCheckingUpdates == true;
    public UpdateCheckState UpdateState => activeSnapshot?.UpdateState ?? UpdateCheckState.UnableToCheck;
    public string UpdateStatusText
    {
        get
        {
            if (UpdateState == UpdateCheckState.UpdateAvailable)
            {
                var names = ComponentCards.Where(x => x.Health == ComponentHealth.Outdated).Select(x => x.Name).ToArray();
                return names.Length == 0 ? "Component update available" :
                    names.Length == 1 ? $"{names[0]} update available" :
                    "Component updates available";
            }

            var hasInstalled = ComponentCards.Any(x => x.Health is ComponentHealth.Installed or ComponentHealth.Outdated);
            return UpdateState switch
            {
                UpdateCheckState.UpToDate when hasInstalled => "Installed sources are current",
                UpdateCheckState.UpToDate => "Official sources checked",
                UpdateCheckState.Checking => "Checking for component updates…",
                UpdateCheckState.Offline => "Could not reach update sources",
                _ => "Unable to check for updates"
            };
        }
    }
    public bool ShowUpdateStatusChip => HasSelection && !IsSelectionLoading &&
        UpdateState is not UpdateCheckState.UnableToCheck;
    public bool HasUpdates => activeSnapshot?.HasUpdates == true;
    public PrimaryActionKind PrimaryAction => activeSnapshot?.PrimaryAction ?? PrimaryActionKind.None;
    public string PrimaryActionText => PrimaryAction switch
    {
        PrimaryActionKind.Install => "Install",
        PrimaryActionKind.Update => "Update",
        PrimaryActionKind.Repair => "Repair and continue",
        PrimaryActionKind.Remove => "Remove",
        _ when IsSelectionLoading => "Detecting…",
        _ when ComponentCards.Any(x => x.Health == ComponentHealth.Installed) => "Installed",
        _ => "Unavailable"
    };
    public string SupportTitle => activeSnapshot?.SupportTitle ?? "Select a game";
    public string SupportMessage => activeSnapshot?.SupportMessage ?? "Choose an installed Steam game to check compatibility.";
    public string ProfileSummary => activeSnapshot?.ProfileSummary ?? "Not resolved";
    public string SelectedProxy => activeSnapshot?.SelectedProxy ?? "Not resolved";
    public string DependencySummary => activeSnapshot?.DependencySummary ?? "Not resolved";
    public string TimingDiagnostics => activeSnapshot?.TimingDiagnostics ?? string.Empty;
    public bool HasTimingDiagnostics => TimingDiagnostics.Length > 0;
    public SelectionPhase SelectionPhase => activeSnapshot?.Phase ?? SelectionPhase.Idle;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasGames => Games.Count > 0;
    public bool HasFilteredGames => FilteredGames.Count > 0;
    public string FilteredGameCount => FilteredGames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool HasSelection => SelectedGame is not null;
    public bool IsSelectionLoading => activeSnapshot?.IsLoading == true;
    public bool ShowGameLoading => HasSelection && IsSelectionLoading;
    public bool UseMotion => !Preferences.ReduceMotion;
    public bool ReduceMotion
    {
        get => Preferences.ReduceMotion;
        set
        {
            if (Preferences.ReduceMotion == value) return;
            Preferences.ReduceMotion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseMotion));
            _ = SavePreferencesAsync();
        }
    }
    public bool CheckForUpdatesAutomatically
    {
        get => Preferences.CheckForUpdatesAutomatically;
        set
        {
            if (Preferences.CheckForUpdatesAutomatically == value) return;
            Preferences.CheckForUpdatesAutomatically = value;
            OnPropertyChanged();
            _ = SavePreferencesAsync();
        }
    }
    public string AdditionalSteamLibrary
    {
        get => Preferences.AdditionalSteamLibrary;
        set
        {
            var normalized = value ?? string.Empty;
            if (Preferences.AdditionalSteamLibrary == normalized) return;
            Preferences.AdditionalSteamLibrary = normalized;
            OnPropertyChanged();
            _ = SavePreferencesAsync();
        }
    }
    public string SelectionProgress => activeSnapshot?.Progress ?? string.Empty;
    public ExecutionResult? PreviousOperationResult => activeSnapshot?.PreviousOperationResult;
    public bool ShowWelcome => CurrentState == UiState.Empty;
    public bool ShowNoGames => CurrentState == UiState.NoGames;
    public bool ShowNoMatches => HasGames && !HasFilteredGames && !IsBusy;
    public bool ShowGame => HasSelection;
    public bool HasWarnings => SelectedGame?.RequiresConfirmation == true || ComponentCards.Any(x => x.Health == ComponentHealth.Conflicting);
    public string WarningText => SelectedGame?.RequiresConfirmation == true
        ? "Anti-cheat files were detected. Deployment requires additional confirmation and may be unsupported."
        : ComponentCards.Any(x => x.Health == ComponentHealth.Conflicting)
            ? "An unknown compatibility file is blocking installation. Advanced details show the occupied filename."
            : string.Empty;
    public string SelectedGameTitle => SelectedGame?.Name ?? "No game selected";
    public string SelectedGameSubtitle => SelectedGame is null ? string.Empty :
        $"Steam AppID {SelectedGame.AppId}  ·  {(SelectedGame.InstallState == SteamInstallState.Installing ? "Installing" : SelectedGame.IsNativeLinux ? "Native Linux / unsupported" : SelectedGame.Engine.ToString())}";
    public string ExecutableDisplay => SelectedGame?.Executable is null ? "Not detected" : Path.GetFileName(SelectedGame.Executable);
    public string ExecutablePath => SelectedGame?.Executable ?? string.Empty;
    public string DeploymentDisplay => SelectedGame is null ? string.Empty : ShortenPath(SelectedGame.DeploymentDirectory);
    public string DeploymentPath => SelectedGame?.DeploymentDirectory ?? string.Empty;
    public string ProtonPrefixDisplay => SelectedGame is null ? string.Empty :
        SelectedGame.HasProtonPrefix ? ShortenPath(SelectedGame.ProtonPrefix) : "Proton prefix not created yet";
    public string ProtonPrefixPath => SelectedGame?.ProtonPrefix ?? string.Empty;
    public string ConfidenceText => SelectedGame is null ? string.Empty : $"{SelectedGame.Confidence} confidence";
    public string ArchitectureDisplay => SelectedGame?.Candidates.FirstOrDefault(x =>
            SelectedGame.Executable is not null &&
            Path.GetFullPath(x.Path).Equals(Path.GetFullPath(SelectedGame.Executable), StringComparison.Ordinal))?.Architecture
            .ToString() ?? SelectedGame?.Candidates.FirstOrDefault()?.Architecture.ToString() ?? "Unknown";
    public string EngineDisplay => SelectedGame?.Engine.ToString() ?? "Unknown";
    public string SelectionReason => SelectedGame?.SelectionReason ?? string.Empty;
    public IReadOnlyList<ExecutableCandidateDisplay> Candidates => SelectedGame?.Candidates.Select(x =>
        new ExecutableCandidateDisplay(x.Score, x.Confidence.ToString(), x.Path, string.Join(" · ", x.Reasons))).ToArray() ?? [];
    public string LaunchOption => activeSnapshot?.RequiredLaunchOption ?? string.Empty;
    public LaunchOptionStatus LaunchOptionStatus => activeSnapshot?.LaunchOptionStatus ?? LaunchOptionStatus.NotDetected;
    public string LaunchOptionStatusText => LaunchOptionStatus switch
    {
        LaunchOptionStatus.Correct => "Correct",
        LaunchOptionStatus.Missing => "Missing",
        LaunchOptionStatus.NeedsUpdate => "Needs update",
        LaunchOptionStatus.NotRequired => "Not required",
        _ => "Not detected"
    };
    public string LaunchOptionExplanation => activeSnapshot?.LaunchOptionExplanation ?? string.Empty;
    public bool HasLaunchOption => LaunchOption.Length > 0;
    public string CacheSizeText { get => cacheSizeText; private set => Set(ref cacheSizeText, value); }
    public string CacheCleanupText { get => cacheCleanupText; private set => Set(ref cacheCleanupText, value); }
    public bool IsCacheBusy { get => isCacheBusy; private set { if (Set(ref isCacheBusy, value)) OnPropertyChanged(nameof(CanManageCache)); } }
    public bool CanManageCache => !IsBusy && !IsCacheBusy;
    public decimal CacheLimitGiB
    {
        get => Preferences.CacheLimitMiB / 1024m;
        set
        {
            var mib = (int)Math.Round(value * 1024m, MidpointRounding.AwayFromZero);
            mib = Math.Clamp(mib, 1, 2_097_151);
            if (Preferences.CacheLimitMiB == mib) return;
            Preferences.CacheLimitMiB = mib;
            OnPropertyChanged();
            _ = SavePreferencesAsync();
        }
    }

    public void ResetSettingsToDefaults()
    {
        Preferences.CacheLimitMiB = 5 * 1024;
        Preferences.ReduceMotion = false;
        Preferences.CheckForUpdatesAutomatically = true;
        Preferences.AdditionalSteamLibrary = string.Empty;
        Preferences.Theme = "System";
        OnPropertyChanged(nameof(CacheLimitGiB));
        OnPropertyChanged(nameof(ReduceMotion));
        OnPropertyChanged(nameof(UseMotion));
        OnPropertyChanged(nameof(CheckForUpdatesAutomatically));
        OnPropertyChanged(nameof(AdditionalSteamLibrary));
        _ = SavePreferencesAsync();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CurrentState = UiState.Loading;
        var busy = BeginBusy();
        GlobalStatus = "Loading saved state…";
        try
        {
            Preferences = await preferencesStore.LoadAsync(cancellationToken);
            if (Preferences.CacheLimitMiB <= 0)
                Preferences.CacheLimitMiB = 5 * 1024;
            searchText = Preferences.SearchText;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(ReduceMotion));
            OnPropertyChanged(nameof(UseMotion));
            OnPropertyChanged(nameof(CacheLimitGiB));
            OnPropertyChanged(nameof(CheckForUpdatesAutomatically));
            OnPropertyChanged(nameof(AdditionalSteamLibrary));
            applicationState = await stateStore.LoadAsync(cancellationToken);
            CurrentState = UiState.Empty;
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) { GlobalStatus = "Scan cancelled"; CurrentState = UiState.Empty; }
        catch (Exception exception) { SetError("Could not initialize", exception); }
        finally { EndBusy(busy); }
        await RefreshCacheStatisticsAsync(cancellationToken);
    }

    public async Task VerifyCacheAsync(CancellationToken cancellationToken = default)
    {
        IsCacheBusy = true;
        GlobalStatus = "Verifying download cache…";
        try
        {
            var entries = await artifactCache.VerifyAsync(cancellationToken);
            var invalid = entries.Count(x => !x.IsValid);
            if (invalid > 0) ErrorMessage = $"{invalid} cache entr{(invalid == 1 ? "y needs" : "ies need")} to be downloaded again.";
            await RefreshCacheStatisticsAsync(cancellationToken);
            if (SelectedGame is not null) await SelectAsync(SelectedGame, cancellationToken);
            GlobalStatus = invalid == 0 ? "Download cache verified" : "Cache verification found invalid files";
        }
        finally { IsCacheBusy = false; }
    }

    public async Task ClearUnusedCacheAsync(CancellationToken cancellationToken = default)
    {
        IsCacheBusy = true;
        GlobalStatus = "Clearing unused downloads…";
        try
        {
            await RefreshArtifactReferencesAsync(games, cancellationToken);
            await stateStore.SaveAsync(applicationState, cancellationToken);
            var references = applicationState.ArtifactReferencesByAppId.Values.SelectMany(x => x)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = await artifactCache.CleanupAsync(references,
                Preferences.CacheLimitMiB * 1024L * 1024L, cancellationToken);
            await RefreshCacheStatisticsAsync(cancellationToken);
            GlobalStatus = $"Freed {FormatBytes(result.BytesRemoved)}";
        }
        finally { IsCacheBusy = false; }
    }

    private async Task RefreshCacheStatisticsAsync(CancellationToken cancellationToken)
    {
        var statistics = await artifactCache.GetStatisticsAsync(cancellationToken);
        CacheSizeText = FormatBytes(statistics.SizeBytes);
        CacheCleanupText = statistics.LastCleanupUtc?.ToLocalTime().ToString("g") ?? "Never";
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (discovery is GameDiscoveryAdapter adapter)
        {
            adapter.ExtraSteamRoots = string.IsNullOrWhiteSpace(Preferences.AdditionalSteamLibrary)
                ? null
                : [Preferences.AdditionalSteamLibrary];
        }
        var previousGames = games.Select(GameIdentity).ToHashSet();
        var selectedAppId = SelectedGame?.AppId ?? Preferences.SelectedAppId;
        var refreshSelectionGeneration = Volatile.Read(ref selectionGeneration);
        if (SelectedGame is { } currentGame)
        {
            long refreshGeneration;
            lock (selectionLock)
            {
                selectionCancellation?.Cancel();
                selectionCancellation?.Dispose();
                refreshGeneration = Interlocked.Increment(ref selectionGeneration);
                selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }
            refreshSelectionGeneration = refreshGeneration;
            ApplySnapshot(GameSelectionSnapshot.Loading(currentGame, refreshGeneration, stackStatusProvider is not null), UiState.Scanning);
        }
        CurrentState = UiState.Scanning;
        var busy = BeginBusy();
        ErrorMessage = null;
        GlobalStatus = "Scanning Steam libraries…";
        try
        {
            var result = await discovery.ScanAsync(applicationState.Overrides, cancellationToken);
            await RefreshArtifactReferencesAsync(applicationState.DiscoveredGames.Concat(result.Games), cancellationToken);
            games = result.Games.ToList();
            applicationState.DiscoveredGames = games;
            applicationState.LastScanUtc = DateTimeOffset.UtcNow;
            await stateStore.SaveAsync(applicationState, cancellationToken);
            OnPropertyChanged(nameof(Games));
            OnPropertyChanged(nameof(HasGames));
            ApplyFilter();
            var selectionChangedDuringScan = refreshSelectionGeneration != Volatile.Read(ref selectionGeneration);
            var targetAppId = selectionChangedDuringScan ? SelectedGame?.AppId : selectedAppId;
            var restored = targetAppId.HasValue ? games.FirstOrDefault(x => x.AppId == targetAppId.Value) : null;
            var next = restored ?? FilteredGames.FirstOrDefault();
            if (next is not null) await SelectAsync(next, cancellationToken);
            else
            {
                ClearSelection();
                CurrentState = games.Count == 0 ? UiState.NoGames : UiState.Ready;
            }
            var currentGames = games.Select(GameIdentity).ToHashSet();
            var added = currentGames.Except(previousGames).Count();
            var removed = previousGames.Except(currentGames).Count();
            GlobalStatus = result.Warnings.Count > 0
                ? $"{games.Count} games found · {result.Warnings.Count} warnings"
                : previousGames.Count > 0 && added == 0 && removed == 0
                    ? $"{games.Count} games found · Steam library is up to date"
                    : $"{games.Count} games found";
        }
        catch (OperationCanceledException) { GlobalStatus = "Scan cancelled"; CurrentState = games.Count == 0 ? UiState.Empty : UiState.Ready; }
        catch (Exception exception) { SetError("Steam scan failed", exception); }
        finally { EndBusy(busy); RaiseStateProperties(); }
    }

    private static string GameIdentity(SteamGame game) =>
        $"{game.AppId}:{Path.GetFullPath(game.GameRoot).TrimEnd(Path.DirectorySeparatorChar)}";

    private async Task RefreshArtifactReferencesAsync(
        IEnumerable<SteamGame> knownGames,
        CancellationToken cancellationToken)
    {
        foreach (var game in knownGames.GroupBy(x => x.AppId).Select(group => group.Last()))
        {
            if (!Directory.Exists(game.GameRoot)) continue;
            try
            {
                var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
                if (manifest.Files.Count > 0 && manifest.AppId != game.AppId) continue;
                applicationState.ArtifactReferencesByAppId[game.AppId] = manifest.Files
                    .SelectMany(file => new[] { file.SourceBlobSha256, file.SourceBundleSha256 })
                    .Where(hash => !string.IsNullOrWhiteSpace(hash))
                    .Select(hash => hash!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or System.Text.Json.JsonException)
            {
                // Preserve the last verified pins when a disconnected library or damaged manifest
                // cannot be inventoried safely.
            }
        }
    }

    public async Task SelectAsync(SteamGame? game, CancellationToken cancellationToken = default)
    {
        if (game is null) return;
        GameSelectionSnapshot? previousSnapshot;
        CancellationTokenSource currentCancellation;
        long generation;
        lock (selectionLock)
        {
            selectionCancellation?.Cancel();
            selectionCancellation?.Dispose();
            generation = Interlocked.Increment(ref selectionGeneration);
            currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            selectionCancellation = currentCancellation;
            previousSnapshot = activeSnapshot;
        }
        var selectionToken = currentCancellation.Token;
        var gameChanged = previousSnapshot?.Game.AppId != game.AppId;
        Preferences.SelectedAppId = game.AppId;
        ApplySnapshot(GameSelectionSnapshot.Loading(game, generation, stackStatusProvider is not null), UiState.Loading);
        SetSelectionStatus($"Detecting {game.Name}…", game.AppId, generation, selectionToken);
        SetSelectionError(null, game.AppId, generation);
        var timing = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var catalog = new GameProfileCatalog(paths);
            var statusTask = statusProvider.DetectAsync(game, selectionToken);
            var profileTask = catalog.MatchAsync(game, selectionToken);
            var proxyTask = new ProxyDiagnosticsService(catalog).DiagnoseAsync(game, selectionToken);
            await Task.WhenAll(statusTask, profileTask, proxyTask);
            if (!IsCurrentSelection(game.AppId, generation, selectionToken)) return;
            var localMs = timing.ElapsedMilliseconds;
            var statuses = await statusTask;
            var profile = await profileTask;
            var proxy = await proxyTask;
            var eligibility = OptiScalerEligibilityService.Evaluate(game, profile, proxy);
            var cards = CreateComponentCards(statuses, null, gameChanged ? null : previousSnapshot);
            var support = BuildSupportSummary(profile, proxy, null, eligibility, false);
            var proposedAction = DeterminePrimaryAction(statuses, false);
            if (proposedAction == PrimaryActionKind.None && support.CanInstall)
                proposedAction = PrimaryActionKind.Install;
            var launch = await ResolveLaunchOptionAsync(game, statuses, proxy, selectionToken);
            var autoUpdates = stackStatusProvider is not null && Preferences.CheckForUpdatesAutomatically;
            var snapshot = new GameSelectionSnapshot(
                game,
                generation,
                false,
                cards,
                $"{profile.Profile.Id} · {profile.MatchReason}",
                ResolveActiveProxyDisplay(statuses, proxy),
                profile.Profile.RenoDx is null
                ? $"OptiScaler: {eligibility.Level} · RenoDX unavailable"
                : $"Full-addon ReShade → {profile.Profile.RenoDx.FileName} · OptiScaler: {eligibility.Level}",
                support.Title,
                support.Message,
                support.CanInstall && proposedAction != PrimaryActionKind.None,
                autoUpdates ? UpdateCheckState.Checking : stackStatusProvider is null
                    ? UpdateCheckState.UnableToCheck : UpdateCheckState.UpToDate,
                false,
                false,
                eligibility.Level,
                proposedAction,
                string.Empty,
                null,
                launch.RequiredOption,
                launch.Status,
                launch.Explanation,
                launch.DetectedOption,
                autoUpdates ? SelectionPhase.CheckingUpdates : SelectionPhase.Idle,
                $"Local detect {localMs} ms · compatibility from catalog/proxy · updates {(autoUpdates ? "background" : "skipped")}");
            if (!IsCurrentSelection(game.AppId, generation, selectionToken)) return;
            var state = cards.Any(x => x.Health == ComponentHealth.Conflicting) ? UiState.Conflict :
                game.RequiresConfirmation ? UiState.Unsupported : UiState.Ready;
            ApplySnapshot(snapshot, state, selectionToken);
            SetSelectionStatus(support.Title, game.AppId, generation, selectionToken);
        }
        catch (OperationCanceledException) when (selectionToken.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            SetSelectionError($"Could not read this game: {exception.Message}", game.AppId, generation, selectionToken, UiState.Error);
        }
        if (!IsCurrentSelection(game.AppId, generation, selectionToken)) return;
        if (stackStatusProvider is not null && Preferences.CheckForUpdatesAutomatically)
            _ = CheckForUpdatesAsync(true, selectionToken);
    }

    public async Task CheckForUpdatesAsync(bool automatic = false, CancellationToken cancellationToken = default)
    {
        if (activeSnapshot is not { } startingSnapshot || stackStatusProvider is null || startingSnapshot.IsCheckingUpdates) return;
        var game = startingSnapshot.Game;
        var generation = startingSnapshot.Generation;
        CancellationToken selectionToken;
        lock (selectionLock) selectionToken = selectionCancellation?.Token ?? CancellationToken.None;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, selectionToken);
        linkedCancellation.CancelAfter(MetadataHttp.UpdateCheckBudget);
        var updateToken = linkedCancellation.Token;
        if (!IsCurrentSelection(game.AppId, generation, updateToken)) return;
        var updateStarted = System.Diagnostics.Stopwatch.StartNew();
        ApplySnapshot(startingSnapshot with
        {
            IsCheckingUpdates = true,
            UpdateState = UpdateCheckState.Checking,
            Phase = SelectionPhase.CheckingUpdates
        }, CurrentState, updateToken);
        if (!automatic) SetSelectionStatus("Checking for component updates…", game.AppId, generation, updateToken);
        try
        {
            var report = await stackStatusProvider.GetAsync(game, true, updateToken, forceRefresh: !automatic);
            if (!IsCurrentSelection(game.AppId, generation, updateToken)) return;
            var hasUpdates = report.Components.Any(x => x.Health == ComponentHealth.Outdated);
            var updateState = report.ArtifactResolution.MetadataState switch
            {
                MetadataCheckState.Offline => UpdateCheckState.Offline,
                _ when !report.ArtifactResolution.IsFullyAutomatic => UpdateCheckState.UnableToCheck,
                MetadataCheckState.Online when hasUpdates => UpdateCheckState.UpdateAvailable,
                MetadataCheckState.Online => UpdateCheckState.UpToDate,
                _ when report.ArtifactResolution.Artifacts.Where(x => x.Component is ComponentKind.ReShade or ComponentKind.RenoDx)
                    .All(x => x.CacheState == ArtifactCacheState.Cached) => UpdateCheckState.Offline,
                _ => UpdateCheckState.UnableToCheck
            };
            var cards = CreateComponentCards(report.Components, report.ArtifactResolution.Components, activeSnapshot);
            var support = BuildSupportSummary(report.Profile, report.Proxy, report, report.OptiScalerEligibility, hasUpdates);
            var proposedAction = DeterminePrimaryAction(report.Components, hasUpdates);
            var primaryAction = proposedAction == PrimaryActionKind.Remove || CanPrepareAutomaticPlan(report, proposedAction) ||
                (proposedAction == PrimaryActionKind.Install && support.CanInstall)
                ? proposedAction
                : PrimaryActionKind.None;
            var launch = await ResolveLaunchOptionAsync(game, report.Components, report.Proxy, updateToken);
            var timing = AppendTiming(startingSnapshot.TimingDiagnostics,
                $"update check {updateStarted.ElapsedMilliseconds} ms · metadata {report.ArtifactResolution.MetadataState}");
            var completed = new GameSelectionSnapshot(
                game,
                generation,
                false,
                cards,
                $"{report.Profile.Profile.Id} · {report.Profile.MatchReason}" +
                    (report.ArtifactResolution.RemoteManifest?.Version is { } manifestVersion ? $" · remote manifest v{manifestVersion}" : string.Empty),
                ResolveActiveProxyDisplay(report.Components, report.Proxy),
                report.Profile.Profile.RenoDx is null
                    ? $"OptiScaler: {report.OptiScalerEligibility.Level} · RenoDX unavailable"
                    : $"Full-addon ReShade → {report.Profile.Profile.RenoDx.FileName} · OptiScaler: {report.OptiScalerEligibility.Level}",
                support.Title,
                support.Message,
                primaryAction != PrimaryActionKind.None,
                updateState,
                false,
                hasUpdates,
                report.OptiScalerEligibility.Level,
                primaryAction,
                string.Empty,
                null,
                launch.RequiredOption,
                launch.Status,
                launch.Explanation,
                launch.DetectedOption,
                SelectionPhase.Idle,
                timing);
            if (!IsCurrentSelection(game.AppId, generation, updateToken)) return;
            if (!ApplySnapshot(completed, cards.Any(x => x.Health == ComponentHealth.Conflicting) ? UiState.Conflict :
                    game.RequiresConfirmation ? UiState.Unsupported : UiState.Ready, updateToken,
                    preserveOperationStatus: automatic)) return;
            if (!automatic || !HasActiveOperationStatus(game.AppId, generation, updateToken))
                SetSelectionStatus(automatic ? SupportTitle : UpdateStatusText, game.AppId, generation, updateToken);
        }
        catch (OperationCanceledException)
        {
            // User cancel, selection switch, or request budget — never leave Checking stuck.
            if (activeSnapshot is { } current &&
                current.Game.AppId == game.AppId &&
                current.Generation == generation)
            {
                ApplySnapshot(current with
                {
                    IsCheckingUpdates = false,
                    UpdateState = selectionToken.IsCancellationRequested
                        ? current.UpdateState == UpdateCheckState.Checking
                            ? UpdateCheckState.UnableToCheck : current.UpdateState
                        : UpdateCheckState.Offline,
                    Phase = SelectionPhase.Idle,
                    TimingDiagnostics = AppendTiming(current.TimingDiagnostics,
                        selectionToken.IsCancellationRequested
                            ? $"update check cancelled after {updateStarted.ElapsedMilliseconds} ms"
                            : $"update check timed out after {updateStarted.ElapsedMilliseconds} ms")
                }, CurrentState);
                if (!automatic && !selectionToken.IsCancellationRequested)
                    SetSelectionStatus("Could not reach update sources", game.AppId, generation);
            }
        }
        catch (HttpRequestException)
        {
            if (activeSnapshot is { } current &&
                current.Game.AppId == game.AppId &&
                current.Generation == generation)
                ApplySnapshot(current with
                {
                    IsCheckingUpdates = false,
                    UpdateState = UpdateCheckState.Offline,
                    Phase = SelectionPhase.Idle
                }, CurrentState);
        }
        catch (Exception exception)
        {
            if (activeSnapshot is { } current &&
                current.Game.AppId == game.AppId &&
                current.Generation == generation)
            {
                ApplySnapshot(current with
                {
                    IsCheckingUpdates = false,
                    UpdateState = UpdateCheckState.UnableToCheck,
                    Phase = SelectionPhase.Idle
                }, CurrentState);
                if (!automatic) SetSelectionError($"Update check failed: {exception.Message}", game.AppId, generation);
            }
        }
        finally
        {
            if (activeSnapshot is { IsCheckingUpdates: true } current &&
                current.Game.AppId == game.AppId &&
                current.Generation == generation)
                ApplySnapshot(current with
                {
                    IsCheckingUpdates = false,
                    Phase = SelectionPhase.Idle,
                    UpdateState = current.UpdateState == UpdateCheckState.Checking
                        ? UpdateCheckState.UnableToCheck : current.UpdateState
                }, CurrentState);
        }
    }

    private static string AppendTiming(string existing, string next) =>
        string.IsNullOrWhiteSpace(existing) ? next : $"{existing} · {next}";

    public async Task SaveOverridesAsync(string? executable, string? deploymentDirectory, CancellationToken cancellationToken = default)
    {
        var game = RequireGame();
        if (string.IsNullOrWhiteSpace(executable) && string.IsNullOrWhiteSpace(deploymentDirectory)) applicationState.Overrides.Remove(game.AppId);
        else applicationState.Overrides[game.AppId] = new GameOverride(executable, deploymentDirectory);
        await stateStore.SaveAsync(applicationState, cancellationToken);
        GlobalStatus = "Game overrides saved";
        await RefreshAsync(cancellationToken);
    }

    public async Task<DeploymentPlan> BuildInstallPlanAsync(ComponentKind component, string source, string action, CancellationToken cancellationToken = default)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linkedCancellation = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var token = linkedCancellation.Token;
        var busy = BeginBusy();
        CurrentState = UiState.Planning;
        GlobalStatus = "Building deployment plan…";
        try
        {
            var full = Path.GetFullPath(source);
            var artifact = new ComponentArtifact(component, full, Path.GetFileName(full));
            var result = action == "repair"
                ? await planner.BuildRepairPlanAsync(snapshot.Game, component, artifact, cancellationToken: token)
                : await planner.BuildInstallPlanAsync(snapshot.Game, artifact, cancellationToken: token);
            EnsureCurrentSelection(snapshot, token);
            if (!SetSelectionStatus("Deployment plan ready for review", snapshot.Game.AppId, snapshot.Generation, token, UiState.Ready))
                throw new OperationCanceledException("The selected game changed while the plan was being prepared.", token);
            return StampPlan(result, snapshot);
        }
        catch (OperationCanceledException)
        {
            SetSelectionStatus("Plan cancelled", snapshot.Game.AppId, snapshot.Generation);
            throw;
        }
        catch
        {
            SetSelectionStatus("Could not prepare deployment", snapshot.Game.AppId, snapshot.Generation);
            throw;
        }
        finally
        {
            EndBusy(busy);
            if (CurrentState == UiState.Planning)
                SetSelectionStatus(GlobalStatus, snapshot.Game.AppId, snapshot.Generation, state: UiState.Ready);
        }
    }

    public async Task<DeploymentPlan> BuildRecommendedStackPlanAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linkedCancellation = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var token = linkedCancellation.Token;
        var busy = BeginBusy();
        CurrentState = UiState.Planning;
        GlobalStatus = "Finding and validating official files…";
        try
        {
            var planningSnapshot = snapshot.PrimaryAction == PrimaryActionKind.None
                ? snapshot with
                {
                    PrimaryAction = DeterminePrimaryAction(snapshot.ComponentCards.Select(x =>
                    new ComponentStatus(x.Component, x.Health, x.Version, [], x.TechnicalExplanation)).ToArray(),
                    snapshot.ComponentCards.Any(x => x.Health == ComponentHealth.Outdated))
                }
                : snapshot;
            var plan = recommendedPlanBuilder is null
                ? await BuildRecommendedStackPlanCoreAsync(planningSnapshot, token)
                : await recommendedPlanBuilder(snapshot.Game, token);
            EnsureCurrentSelection(snapshot, token);
            var result = StampPlan(ClonePlanWithAction(plan, planningSnapshot.PrimaryAction), snapshot);
            if (!SetSelectionStatus("Recommended stack ready for review", snapshot.Game.AppId, snapshot.Generation, token, UiState.Ready))
                throw new OperationCanceledException("The selected game changed while the plan was being prepared.", token);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetSelectionStatus("Plan cancelled", snapshot.Game.AppId, snapshot.Generation);
            throw;
        }
        catch
        {
            SetSelectionStatus("Could not prepare setup", snapshot.Game.AppId, snapshot.Generation);
            throw;
        }
        finally
        {
            EndBusy(busy);
            if (CurrentState == UiState.Planning)
                SetSelectionStatus(GlobalStatus, snapshot.Game.AppId, snapshot.Generation, state: UiState.Ready);
        }
    }

    public async Task<DeploymentPlan> BuildAutomaticComponentPlanAsync(
        ComponentKind component,
        CancellationToken cancellationToken = default)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        var card = snapshot.ComponentCards.Single(x => x.Component == component);
        var componentAction = card.Health switch
        {
            ComponentHealth.Outdated => PrimaryActionKind.Update,
            ComponentHealth.Broken or ComponentHealth.PartiallyInstalled => PrimaryActionKind.Repair,
            ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable when card.CanRepair => PrimaryActionKind.Repair,
            _ => PrimaryActionKind.Install
        };
        var planningSnapshot = snapshot with { PrimaryAction = componentAction };
        using var linkedCancellation = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var token = linkedCancellation.Token;
        var busy = BeginBusy();
        CurrentState = UiState.Planning;
        GlobalStatus = $"Finding and validating official {card.Name} files…";
        try
        {
            var plan = recommendedPlanBuilder is null
                ? await BuildRecommendedStackPlanCoreAsync(planningSnapshot, token, component)
                : await recommendedPlanBuilder(snapshot.Game, token);
            EnsureCurrentSelection(snapshot, token);
            var result = StampPlan(ClonePlanWithAction(plan, componentAction), snapshot);
            if (!SetSelectionStatus($"{card.Name} plan ready for review", snapshot.Game.AppId,
                    snapshot.Generation, token, UiState.Ready))
                throw new OperationCanceledException(
                    "The selected game changed while the plan was being prepared.", token);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetSelectionStatus("Plan cancelled", snapshot.Game.AppId, snapshot.Generation);
            throw;
        }
        catch
        {
            SetSelectionStatus("Could not prepare setup", snapshot.Game.AppId, snapshot.Generation);
            throw;
        }
        finally
        {
            EndBusy(busy);
            if (CurrentState == UiState.Planning)
                SetSelectionStatus(GlobalStatus, snapshot.Game.AppId, snapshot.Generation, state: UiState.Ready);
        }
    }

    public Task<DeploymentPlan> BuildPrimaryActionPlanAsync(CancellationToken cancellationToken = default) =>
        PrimaryAction == PrimaryActionKind.Remove
            ? BuildRemoveStackPlanAsync(cancellationToken)
            : BuildRecommendedStackPlanAsync(cancellationToken);

    private async Task<DeploymentPlan> BuildRemoveStackPlanAsync(CancellationToken cancellationToken)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linkedCancellation = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var busy = BeginBusy();
        CurrentState = UiState.Planning;
        GlobalStatus = "Building removal plan…";
        try
        {
            var result = StampPlan(await planner.BuildRemoveStackPlanAsync(snapshot.Game, linkedCancellation.Token), snapshot);
            EnsureCurrentSelection(snapshot, linkedCancellation.Token);
            if (!SetSelectionStatus("Removal plan ready for review", snapshot.Game.AppId, snapshot.Generation,
                    linkedCancellation.Token, UiState.Ready))
                throw new OperationCanceledException("The selected game changed while the plan was being prepared.", linkedCancellation.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetSelectionStatus("Plan cancelled", snapshot.Game.AppId, snapshot.Generation);
            throw;
        }
        catch
        {
            SetSelectionStatus("Could not prepare removal", snapshot.Game.AppId, snapshot.Generation);
            throw;
        }
        finally
        {
            EndBusy(busy);
            if (CurrentState == UiState.Planning)
                SetSelectionStatus(GlobalStatus, snapshot.Game.AppId, snapshot.Generation, state: UiState.Ready);
        }
    }

    public async Task<DeploymentPlan> BuildRemovePlanAsync(ComponentKind component, CancellationToken cancellationToken = default)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linkedCancellation = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var result = StampPlan(
            await planner.BuildRemovePlanAsync(snapshot.Game, component, cancellationToken: linkedCancellation.Token),
            snapshot);
        EnsureCurrentSelection(snapshot, linkedCancellation.Token);
        return result;
    }

    public async Task<DeploymentPlan> BuildRestorePlanAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linkedCancellation = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var result = StampPlan(await planner.BuildRestorePlanAsync(snapshot.Game, linkedCancellation.Token), snapshot);
        EnsureCurrentSelection(snapshot, linkedCancellation.Token);
        return result;
    }

    private async Task<DeploymentPlan> BuildRecommendedStackPlanCoreAsync(
        GameSelectionSnapshot snapshot,
        CancellationToken cancellationToken,
        ComponentKind? requestedComponent = null)
    {
        var game = snapshot.Game;
        var catalog = new GameProfileCatalog(paths);
        using var httpClient = new HttpClient();
        var resolver = new OfficialArtifactResolver(httpClient, paths, catalog);
        var resolution = await resolver.ResolveAsync(game, true, cancellationToken, forceRefresh: true);
        var requestReno = (requestedComponent is null or ComponentKind.RenoDx) &&
            resolution.CanAcquireRenoSetup && ShouldTargetComponent(snapshot, ComponentKind.RenoDx) &&
            HasSafeRenoDependency(snapshot);
        var requestIndependentReShade = (requestedComponent is null or ComponentKind.ReShade) &&
            (requestedComponent == ComponentKind.ReShade ||
             snapshot.PrimaryAction is PrimaryActionKind.Update or PrimaryActionKind.Repair) &&
            ShouldTargetComponent(snapshot, ComponentKind.ReShade) &&
            resolution.Artifacts.Any(x => x.Component == ComponentKind.ReShade);
        var requestOpti = (requestedComponent is null or ComponentKind.OptiScaler) &&
            resolution.CanAcquireOptiScaler && ShouldTargetComponent(snapshot, ComponentKind.OptiScaler);
        if (!requestIndependentReShade && !requestReno && !requestOpti)
            throw new InvalidOperationException("No component in the selected game state has a safe automatic deployment target.");

        var targetComponents = new HashSet<ComponentKind>();
        if (requestIndependentReShade || requestReno) targetComponents.Add(ComponentKind.ReShade);
        if (requestReno) targetComponents.Add(ComponentKind.RenoDx);
        if (requestOpti) targetComponents.Add(ComponentKind.OptiScaler);
        resolution = await resolver.AcquireSelectedAsync(resolution, targetComponents, true, cancellationToken);
        var includeReno = requestReno && resolution.CanAcquireRenoSetup;
        var includeOpti = requestOpti && resolution.CanAcquireOptiScaler;
        var includeReShade = includeReno || requestIndependentReShade && resolution.Artifacts.Any(x =>
            x.Component == ComponentKind.ReShade && x.CacheState == ArtifactCacheState.Cached);
        if (!includeReShade && !includeReno && !includeOpti)
            throw new InvalidOperationException(string.Join(" ", resolution.Warnings));

        var proxy = await new ProxyDiagnosticsService(catalog).DiagnoseAsync(game, cancellationToken);
        ComponentArtifact Required(ComponentKind component)
        {
            var selection = resolution.Artifacts.SingleOrDefault(x => x.Component == component && x.CachedPath is not null)
                ?? throw new InvalidOperationException($"The required {component} file is unavailable.");
            return CachedArtifactMaterializer.MaterializeSingle(selection);
        }
        var opti = resolution.Artifacts.SingleOrDefault(x => x.Component == ComponentKind.OptiScaler);
        (ComponentArtifact Main, IReadOnlyList<ComponentArtifact> Support, OptiScalerBundleManifest Manifest)? materializedOpti = null;
        if (includeOpti && opti is not null)
            materializedOpti = await CachedArtifactMaterializer.MaterializeOptiScalerAsync(
                artifactCache, opti, cancellationToken);
        return await planner.BuildRecommendedStackPlanAsync(game,
            new(includeReShade ? Required(ComponentKind.ReShade) : null,
                includeReno ? Required(ComponentKind.RenoDx) : null,
                includeOpti ? materializedOpti?.Main : null,
                includeOpti ? materializedOpti?.Support : null,
                includeOpti ? materializedOpti?.Manifest : null),
            proxy, cancellationToken);
    }

    public async Task<ExecutionResult> ExecuteAsync(DeploymentPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        var operationSnapshot = activeSnapshot;
        if (operationSnapshot is null || !PlanMatchesSnapshot(plan, operationSnapshot))
            return new ExecutionResult(false, dryRun, false, [],
                "The selected game changed before the operation began. No files were changed.");

        var operationContext = OperationContext.From(operationSnapshot.Game, operationSnapshot.Generation);
        var operationAppId = operationContext.AppId;
        var operationGeneration = operationContext.SelectionGeneration;
        var busy = BeginBusy();
        var operationStatus = dryRun ? "Validating operation…" : "Applying changes…";
        if (!ApplySnapshot(operationSnapshot with { Progress = operationStatus, PreviousOperationResult = null }, UiState.Deploying))
        {
            EndBusy(busy);
            return new ExecutionResult(false, dryRun, false, [],
                "The selected game changed before the operation began. No files were changed.");
        }
        SetSelectionStatus(operationStatus, operationAppId, operationGeneration);
        var attempted = false;
        var cancelled = false;
        var rescannedCurrentGame = false;
        ExecutionResult result;
        try
        {
            attempted = true;
            result = await executor.ExecuteAsync(plan, dryRun, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            result = new ExecutionResult(false, dryRun, false, [], "Operation cancelled.");
        }
        catch (Exception exception)
        {
            result = new ExecutionResult(false, dryRun, false, [], exception.Message);
        }

        try
        {
            if (attempted && !dryRun && IsCurrentSelection(operationAppId, operationGeneration) && SelectedGame is { } selected)
            {
                await SelectAsync(selected);
                rescannedCurrentGame = activeSnapshot is { } afterScan &&
                    PlanMatchesSnapshot(plan, afterScan);
                if (result.Succeeded && !await PostOperationStateMatchesAsync(plan, activeSnapshot))
                    result = result with
                    {
                        Succeeded = false,
                        Error = "Post-operation detection did not confirm every requested component state."
                    };
            }

            if (activeSnapshot is { } completedSnapshot &&
                PlanMatchesSnapshot(plan, completedSnapshot) &&
                (rescannedCurrentGame || completedSnapshot.Generation == operationGeneration))
            {
                var completedState = cancelled ? UiState.Ready : result.Succeeded ? UiState.Success : UiState.Error;
                ApplySnapshot(completedSnapshot with { Progress = string.Empty, PreviousOperationResult = result }, completedState);
                SetSelectionError(cancelled ? null : result.Error, completedSnapshot.Game.AppId, completedSnapshot.Generation);
                SetSelectionStatus(cancelled ? "Operation cancelled" : result.DryRun ? "Validation complete" :
                    result.Succeeded
                        ? (plan.Action.Contains("restore", StringComparison.OrdinalIgnoreCase)
                            ? "The game files were restored."
                            : "Changes completed")
                        : "Operation failed",
                    completedSnapshot.Game.AppId, completedSnapshot.Generation);
            }
            return result;
        }
        finally
        {
            EndBusy(busy);
            if (activeSnapshot is { } current &&
                current.Game.AppId == operationAppId &&
                (current.Generation == operationGeneration || rescannedCurrentGame))
                ApplySnapshot(current with { Progress = string.Empty }, CurrentState);
        }
    }

    public bool IsPlanForCurrentSelection(DeploymentPlan plan) =>
        activeSnapshot is { } snapshot && PlanMatchesSnapshot(plan, snapshot);

    public void CancelBackgroundWork()
    {
        lock (selectionLock)
        {
            selectionCancellation?.Cancel();
            selectionCancellation?.Dispose();
            selectionCancellation = null;
            var generation = Interlocked.Increment(ref selectionGeneration);
            if (activeSnapshot is { } snapshot)
                activeSnapshot = snapshot with
                {
                    Generation = generation,
                    IsLoading = false,
                    Progress = string.Empty,
                    PreviousOperationResult = null
                };
        }
    }

    public void CollapseComponentDetails() => CollapseAllDetails();

    public Task SavePreferencesAsync(CancellationToken cancellationToken = default) => preferencesStore.SaveAsync(Preferences, cancellationToken);

    public void DismissError()
    {
        ErrorMessage = null;
        if (CurrentState == UiState.Error) CurrentState = SelectedGame is null ? UiState.Empty : UiState.Ready;
    }

    private void ApplyFilter()
    {
        FilteredGames = GameSearch.Rank(games, SearchText);
        OnPropertyChanged(nameof(FilteredGameCount));
        RaiseStateProperties();
    }

    private IReadOnlyList<ComponentCardViewModel> CreateComponentCards(
        IReadOnlyList<ComponentStatus> statuses,
        IReadOnlyList<ResolvedArtifact>? resolved,
        GameSelectionSnapshot? expansionSource)
    {
        var expandedCard = expansionSource?.ComponentCards.SingleOrDefault(x => x.IsDetailsExpanded);
        var cards = statuses.Select(status => new ComponentCardViewModel(status,
            resolved?.SingleOrDefault(x => x.Component == status.Component), ExpandDetails)).ToArray();
        var nextExpanded = expandedCard is null ? null : cards.SingleOrDefault(x =>
            x.Component == expandedCard.Component && x.MaterialSignature == expandedCard.MaterialSignature);
        if (nextExpanded is not null) nextExpanded.IsDetailsExpanded = true;
        return cards;
    }

    private void ExpandDetails(ComponentCardViewModel selected)
    {
        foreach (var card in ComponentCards)
            if (!ReferenceEquals(card, selected) && card.IsDetailsExpanded) card.IsDetailsExpanded = false;
    }

    private void CollapseAllDetails()
    {
        foreach (var card in ComponentCards) card.IsDetailsExpanded = false;
    }

    private static SupportSummary BuildSupportSummary(
        GameProfileMatch profile,
        ProxySelectionResult proxy,
        StackStatusReport? report,
        OptiScalerEligibility eligibility,
        bool hasUpdates)
    {
        if (!proxy.HasSafeProxy)
            return new("Installation blocked",
                "Another file already uses every safe compatibility filename. Remove or identify it, then refresh.", false);
        if (profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported && !eligibility.CanInstall)
            return new(GameSummary(eligibility.Explanation),
                HumanizeSupportDetail(eligibility.Explanation) ?? eligibility.Explanation, false);
        if (report is null)
            return BuildLocalSupportSummary(profile, eligibility);
        if (!report.ArtifactResolution.IsFullyAutomatic)
        {
            var local = BuildLocalSupportSummary(profile, eligibility);
            if (local.CanInstall)
                return local;
            return new(
                report.ArtifactResolution.MetadataState == MetadataCheckState.UnableToCheck
                    ? "Could not check required files"
                    : "Required files unavailable",
                HumanizeSupportDetail(report.ArtifactResolution.Warnings.FirstOrDefault()) ??
                    "The required files could not be prepared automatically.",
                false);
        }
        if (report.Components.Any(x => x.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled ||
            x.Health is ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable &&
            x.Verification == InstallationVerification.RepairNeeded))
            return new("Repair recommended",
                "Some files from an earlier installation need to be repaired before continuing.",
                report.CanInstallRecommendedStack);
        if (hasUpdates)
            return new("Update available",
                "Newer official files are available. Review the update plan before applying it.",
                report.CanInstallRecommendedStack);
        if (report.Components.Any(x => x.Health == ComponentHealth.Installed))
            return new("Already installed",
                "The managed setup is installed and its files look consistent.",
                report.CanInstallRecommendedStack);
        var alternative = proxy.Candidates.Any(x => !x.SafeForNewInstallation && File.Exists(x.Path));
        return new(
            eligibility.Level == OptiScalerCompatibilityLevel.Experimental &&
                profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported ? "Experimental setup" :
                alternative ? "Ready to install" : "Ready to install",
            alternative
                ? "A safe compatibility filename is available without changing existing game files."
                : LocalReadyMessage(profile, eligibility),
            report.CanInstallRecommendedStack);
    }

    private static SupportSummary BuildLocalSupportSummary(
        GameProfileMatch profile,
        OptiScalerEligibility eligibility)
    {
        if (profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported && eligibility.CanInstall)
            return new(
                eligibility.Level == OptiScalerCompatibilityLevel.Experimental ? "Experimental setup" : "Ready to install",
                HumanizeSupportDetail(eligibility.Explanation) ??
                    "No RenoDX addon was found for this game. ReShade and OptiScaler are still available.",
                true);
        if (profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported)
            return new("Unsupported",
                "No RenoDX addon was found for this game. ReShade and OptiScaler are still available when supported.",
                false);

        return new(
            "Ready to install",
            LocalReadyMessage(profile, eligibility),
            true);
    }

    private static string LocalReadyMessage(GameProfileMatch profile, OptiScalerEligibility eligibility) =>
        profile.Profile.RenoDxSupport switch
        {
            GameProfileSupport.EngineFallback when profile.Profile.Engine == GameEngine.Unreal =>
                "This game can use the generic Unreal RenoDX addon.",
            GameProfileSupport.EngineFallback when profile.Profile.Engine == GameEngine.Unity =>
                "This game can use the generic Unity RenoDX addon.",
            GameProfileSupport.EngineFallback =>
                $"This game can use the generic {profile.Profile.Engine} RenoDX addon.",
            GameProfileSupport.Unsupported =>
                HumanizeSupportDetail(eligibility.Explanation) ??
                    "No RenoDX addon was found for this game. ReShade and OptiScaler are still available.",
            _ => "An exact RenoDX setup is available for this game."
        };

    private static string? HumanizeSupportDetail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("Legacy Unreal", StringComparison.OrdinalIgnoreCase))
            return "This Unreal version is too old for the generic RenoDX addon.";
        if (text.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase))
            return "Anti-cheat files were detected. Confirm carefully before installing anything.";
        if (text.Contains("No exact AppID", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no approved engine", StringComparison.OrdinalIgnoreCase))
            return "No RenoDX addon was found for this game. ReShade and OptiScaler are still available.";
        if (text.Contains("dependency-aware", StringComparison.OrdinalIgnoreCase))
            return "An update is available for installed components.";
        if (text.Contains("A compatible installation method was found", StringComparison.OrdinalIgnoreCase))
            return "A safe compatibility filename is available without changing existing game files.";
        return text;
    }

    private static PrimaryActionKind DeterminePrimaryAction(
        IReadOnlyList<ComponentStatus> components,
        bool hasUpdates)
    {
        var needsRepair = components.Any(x =>
            x.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled ||
            (x.Health is ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable &&
             x.Verification == InstallationVerification.RepairNeeded));
        if (needsRepair) return PrimaryActionKind.Repair;
        if (hasUpdates) return PrimaryActionKind.Update;
        if (components.Any(x => x.Health is ComponentHealth.Available or ComponentHealth.Supported or
                ComponentHealth.DownloadRequired or ComponentHealth.Cached or ComponentHealth.Experimental))
            return PrimaryActionKind.Install;
        if (components.Any(x => x.Health == ComponentHealth.Installed)) return PrimaryActionKind.Remove;
        return PrimaryActionKind.None;
    }

    private static bool CanPrepareAutomaticPlan(StackStatusReport report, PrimaryActionKind action)
    {
        if (action is not (PrimaryActionKind.Install or PrimaryActionKind.Update or PrimaryActionKind.Repair)) return false;
        var components = report.Components;
        var reno = (report.ArtifactResolution.CanAcquireRenoSetup ||
                report.Profile.Profile.RenoDxSupport is GameProfileSupport.Supported or GameProfileSupport.EngineFallback) &&
            ShouldTargetComponent(components, ComponentKind.RenoDx, action) && HasSafeRenoDependency(components);
        var reshade = action is PrimaryActionKind.Update or PrimaryActionKind.Repair &&
            ShouldTargetComponent(components, ComponentKind.ReShade, action) &&
            report.ArtifactResolution.Artifacts.Any(x => x.Component == ComponentKind.ReShade);
        var opti = report.OptiScalerEligibility.CanInstall &&
            (report.ArtifactResolution.CanAcquireOptiScaler || report.OptiScalerEligibility.CanInstall) &&
            ShouldTargetComponent(components, ComponentKind.OptiScaler, action);
        return reno || reshade || opti;
    }

    private static bool ShouldTargetComponent(GameSelectionSnapshot snapshot, ComponentKind component) =>
        ShouldTargetComponent(snapshot.ComponentCards.Select(x =>
            new ComponentStatus(x.Component, x.Health, x.Version, [], x.TechnicalExplanation)).ToArray(),
            component, snapshot.PrimaryAction);

    private static bool ShouldTargetComponent(
        IReadOnlyList<ComponentStatus> components,
        ComponentKind component,
        PrimaryActionKind action)
    {
        var health = components.SingleOrDefault(x => x.Component == component)?.Health;
        return action switch
        {
            PrimaryActionKind.Install => health is ComponentHealth.Available or ComponentHealth.Supported or
                ComponentHealth.DownloadRequired or ComponentHealth.Cached or ComponentHealth.Experimental,
            PrimaryActionKind.Update => health == ComponentHealth.Outdated,
            PrimaryActionKind.Repair => health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable,
            _ => false
        };
    }

    private static bool HasSafeRenoDependency(GameSelectionSnapshot snapshot) =>
        HasSafeRenoDependency(snapshot.ComponentCards.Select(x =>
            new ComponentStatus(x.Component, x.Health, x.Version, [], x.TechnicalExplanation)).ToArray());

    private static bool HasSafeRenoDependency(IReadOnlyList<ComponentStatus> components) =>
        components.SingleOrDefault(x => x.Component == ComponentKind.ReShade)?.Health is { } health &&
        health is not (ComponentHealth.ForeignInstallation or ComponentHealth.Conflicting or ComponentHealth.MissingDependency or
            ComponentHealth.ManifestUnavailable or ComponentHealth.Unsupported or ComponentHealth.Unavailable);

    private sealed record SupportSummary(string Title, string Message, bool CanInstall);

    private static string GameSummary(string reason) => reason.Contains("executable", StringComparison.OrdinalIgnoreCase)
        ? "Unable to determine the correct game executable" : "Unsupported game";

    private bool ApplySnapshot(
        GameSelectionSnapshot snapshot,
        UiState state,
        CancellationToken cancellationToken = default,
        bool preserveOperationStatus = false)
    {
        bool stateChanged;
        lock (selectionLock)
        {
            if (cancellationToken.IsCancellationRequested || snapshot.Generation != Volatile.Read(ref selectionGeneration))
                return false;
            if (preserveOperationStatus && activeSnapshot is { } current &&
                (current.PreviousOperationResult is not null || current.Progress.Length > 0 || currentState == UiState.Deploying))
            {
                snapshot = snapshot with
                {
                    Progress = current.Progress,
                    PreviousOperationResult = current.PreviousOperationResult
                };
                state = currentState;
            }
            activeSnapshot = snapshot;
            stateChanged = currentState != state;
            currentState = state;
        }
        if (stateChanged)
        {
            OnPropertyChanged(nameof(CurrentState));
            RaiseStateProperties();
        }
        RaiseSelectionProperties();
        return true;
    }

    private bool HasActiveOperationStatus(uint appId, long generation, CancellationToken cancellationToken)
    {
        lock (selectionLock)
        {
            return !cancellationToken.IsCancellationRequested &&
                activeSnapshot?.Game.AppId == appId &&
                activeSnapshot.Generation == generation &&
                generation == Volatile.Read(ref selectionGeneration) &&
                (activeSnapshot.PreviousOperationResult is not null ||
                 activeSnapshot.Progress.Length > 0 || currentState == UiState.Deploying);
        }
    }

    private void ClearSelection()
    {
        lock (selectionLock)
        {
            selectionCancellation?.Cancel();
            selectionCancellation?.Dispose();
            selectionCancellation = null;
            Interlocked.Increment(ref selectionGeneration);
            activeSnapshot = null;
        }
        ErrorMessage = null;
        RaiseSelectionProperties();
    }

    private bool IsCurrentSelection(uint? appId, long generation, CancellationToken cancellationToken = default) =>
        !cancellationToken.IsCancellationRequested &&
        appId.HasValue &&
        activeSnapshot?.Game.AppId == appId.Value &&
        activeSnapshot.Generation == generation &&
        generation == Volatile.Read(ref selectionGeneration);

    private bool SetSelectionError(
        string? message,
        uint? appId,
        long generation,
        CancellationToken cancellationToken = default,
        UiState? state = null)
    {
        bool errorChanged;
        bool stateChanged;
        lock (selectionLock)
        {
            if (cancellationToken.IsCancellationRequested || !appId.HasValue ||
                activeSnapshot?.Game.AppId != appId.Value || activeSnapshot.Generation != generation ||
                generation != Volatile.Read(ref selectionGeneration)) return false;
            errorChanged = errorMessage != message;
            errorMessage = message;
            stateChanged = state.HasValue && currentState != state.Value;
            if (state.HasValue) currentState = state.Value;
        }
        if (errorChanged)
        {
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(HasError));
        }
        if (stateChanged)
        {
            OnPropertyChanged(nameof(CurrentState));
            RaiseStateProperties();
        }
        return true;
    }

    private bool SetSelectionStatus(
        string status,
        uint appId,
        long generation,
        CancellationToken cancellationToken = default,
        UiState? state = null)
    {
        bool statusChanged;
        bool stateChanged;
        lock (selectionLock)
        {
            if (cancellationToken.IsCancellationRequested || activeSnapshot?.Game.AppId != appId ||
                activeSnapshot.Generation != generation || generation != Volatile.Read(ref selectionGeneration)) return false;
            statusChanged = globalStatus != status;
            globalStatus = status;
            stateChanged = state.HasValue && currentState != state.Value;
            if (state.HasValue) currentState = state.Value;
        }
        if (statusChanged) OnPropertyChanged(nameof(GlobalStatus));
        if (stateChanged)
        {
            OnPropertyChanged(nameof(CurrentState));
            RaiseStateProperties();
        }
        return true;
    }

    private static async Task<bool> PostOperationStateMatchesAsync(DeploymentPlan plan, GameSelectionSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.IsLoading || !PlanMatchesSnapshot(plan, snapshot)) return false;
        if (plan.ExpectedComponentStates.Count == 0)
            return plan.Action.Contains("restore", StringComparison.OrdinalIgnoreCase) &&
                snapshot.ComponentCards.All(x => x.Health is not (ComponentHealth.Broken or
                    ComponentHealth.PartiallyInstalled or ComponentHealth.IncorrectlyConfigured or
                    ComponentHealth.RepairAvailable or ComponentHealth.ManifestUnavailable));
        if (plan.ExpectedComponentStates.GroupBy(x => x.Component)
            .Any(group => group.Select(x => x.Installed).Distinct().Count() != 1)) return false;

        foreach (var expectation in plan.ExpectedComponentStates.Distinct())
        {
            var card = snapshot.ComponentCards.SingleOrDefault(x => x.Component == expectation.Component);
            if (card is null && expectation is { Component: ComponentKind.OptiPatcher, Installed: false })
            {
                try
                {
                    var manifest = await ComponentDetector.LoadManifestAsync(snapshot.Game.GameRoot);
                    if (manifest.Files.Any(x => x.Component == ComponentKind.OptiPatcher)) return false;
                    continue;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                {
                    return false;
                }
            }
            if (card is null) return false;
            if (expectation.Installed)
            {
                if (card.Health != ComponentHealth.Installed) return false;
            }
            else if (card.Health is not (ComponentHealth.Unavailable or ComponentHealth.Available or
                         ComponentHealth.Supported or ComponentHealth.DownloadRequired or ComponentHealth.Cached or
                         ComponentHealth.MissingDependency or ComponentHealth.Unsupported or ComponentHealth.Conflicting or
                         ComponentHealth.ForeignInstallation or ComponentHealth.Experimental))
            {
                return false;
            }
        }
        return true;
    }

    private CancellationTokenSource CreateSelectionLinkedCancellation(
        GameSelectionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        lock (selectionLock)
        {
            if (!IsCurrentSelection(snapshot.Game.AppId, snapshot.Generation))
                throw new OperationCanceledException("The selected game changed while the plan was being prepared.");
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                selectionCancellation?.Token ?? CancellationToken.None);
        }
    }

    private void EnsureCurrentSelection(GameSelectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!IsCurrentSelection(snapshot.Game.AppId, snapshot.Generation, cancellationToken))
            throw new OperationCanceledException("The selected game changed while the plan was being prepared.", cancellationToken);
    }

    private static bool PlanMatchesSnapshot(DeploymentPlan plan, GameSelectionSnapshot snapshot) =>
        plan.AppId == snapshot.Game.AppId &&
        (plan.SelectionGeneration == 0 || plan.SelectionGeneration == snapshot.Generation) &&
        PathsEqual(plan.GameRoot, snapshot.Game.GameRoot) &&
        PathsEqual(plan.DeploymentDirectory, snapshot.Game.DeploymentDirectory);

    private static DeploymentPlan StampPlan(DeploymentPlan plan, GameSelectionSnapshot snapshot)
    {
        plan.SelectionGeneration = snapshot.Generation;
        return plan;
    }

    private static async Task<LaunchOptionObservation> ResolveLaunchOptionAsync(
        SteamGame game,
        IReadOnlyList<ComponentStatus> statuses,
        ProxySelectionResult proxy,
        CancellationToken cancellationToken)
    {
        var requiredProxy = ResolveManagedProxyName(statuses) ??
            (statuses.Any(x =>
                (x.Component is ComponentKind.ReShade or ComponentKind.OptiScaler) &&
                (x.Health is ComponentHealth.Installed or ComponentHealth.Outdated or
                    ComponentHealth.PartiallyInstalled or ComponentHealth.Broken))
                ? proxy.SelectedProxy
                : null);
        var required = requiredProxy is not null && requiredProxy.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? DeploymentPlanner.GenerateLaunchOption(requiredProxy)
            : string.Empty;
        string? detected = null;
        try { detected = await SteamLaunchOptionService.TryReadLaunchOptionsAsync(game, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { detected = null; }
        return SteamLaunchOptionService.Observe(game, required, detected);
    }

    private static string ResolveActiveProxyDisplay(
        IReadOnlyList<ComponentStatus> statuses,
        ProxySelectionResult proxy) =>
        ResolveManagedProxyName(statuses) ?? proxy.SelectedProxy ?? "No safe proxy";

    private static string? ResolveManagedProxyName(IReadOnlyList<ComponentStatus> statuses)
    {
        foreach (var component in new[] { ComponentKind.OptiScaler, ComponentKind.ReShade })
        {
            var status = statuses.FirstOrDefault(x => x.Component == component);
            if (status?.Health is not (ComponentHealth.Installed or ComponentHealth.Outdated or
                ComponentHealth.PartiallyInstalled or ComponentHealth.Broken)) continue;
            var proxy = status.Files.FirstOrDefault(file =>
                DeploymentPlanner.SupportedProxyNames.Contains(file, StringComparer.OrdinalIgnoreCase));
            if (proxy is not null) return proxy;
        }
        return null;
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.Ordinal);

    private static DeploymentPlan ClonePlanWithAction(DeploymentPlan plan, PrimaryActionKind action) => new()
    {
        Id = plan.Id,
        AppId = plan.AppId,
        GameRoot = plan.GameRoot,
        DeploymentDirectory = plan.DeploymentDirectory,
        SelectionGeneration = plan.SelectionGeneration,
        Action = action switch
        {
            PrimaryActionKind.Update => "update installed components",
            PrimaryActionKind.Repair => "repair recommended setup",
            PrimaryActionKind.Remove => "remove recommended setup",
            _ => "install recommended setup"
        },
        RequiresConfirmation = plan.RequiresConfirmation,
        RequiresRepair = plan.RequiresRepair || action == PrimaryActionKind.Repair,
        CompatibilityMessage = plan.CompatibilityMessage,
        LaunchOption = plan.LaunchOption,
        Warnings = plan.Warnings,
        Operations = plan.Operations,
        FileDecisions = plan.FileDecisions,
        ExpectedComponentStates = plan.ExpectedComponentStates
    };

    private void SetError(string context, Exception exception)
    {
        ErrorMessage = $"{context}: {exception.Message}";
        GlobalStatus = context;
        CurrentState = UiState.Error;
    }

    private SteamGame RequireGame() => SelectedGame ?? throw new InvalidOperationException("Select a game first.");
    private void RaiseSelectionProperties()
    {
        foreach (var property in new[] { nameof(SelectedGame), nameof(ComponentCards), nameof(HasSelection), nameof(ShowGame), nameof(IsSelectionLoading), nameof(ShowGameLoading), nameof(HasWarnings), nameof(WarningText), nameof(SelectedGameTitle), nameof(SelectedGameSubtitle), nameof(ExecutableDisplay), nameof(ExecutablePath), nameof(DeploymentDisplay), nameof(DeploymentPath), nameof(ProtonPrefixDisplay), nameof(ProtonPrefixPath), nameof(ConfidenceText), nameof(ArchitectureDisplay), nameof(EngineDisplay), nameof(SelectionReason), nameof(Candidates), nameof(LaunchOption), nameof(LaunchOptionStatus), nameof(LaunchOptionStatusText), nameof(LaunchOptionExplanation), nameof(HasLaunchOption), nameof(ProfileSummary), nameof(SelectedProxy), nameof(DependencySummary), nameof(TimingDiagnostics), nameof(HasTimingDiagnostics), nameof(SelectionPhase), nameof(SupportTitle), nameof(SupportMessage), nameof(CanInstallRecommendedStack), nameof(CanCheckForUpdates), nameof(IsCheckingUpdates), nameof(UpdateState), nameof(UpdateStatusText), nameof(ShowUpdateStatusChip), nameof(HasUpdates), nameof(PrimaryAction), nameof(PrimaryActionText), nameof(SelectionProgress), nameof(PreviousOperationResult) }) OnPropertyChanged(property);
    }
    private void RaiseStateProperties()
    {
        foreach (var property in new[] { nameof(ShowWelcome), nameof(ShowNoGames), nameof(ShowNoMatches), nameof(ShowGame), nameof(HasWarnings), nameof(WarningText), nameof(HasFilteredGames) }) OnPropertyChanged(property);
    }
    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024d / 1024d:0.##} GiB"
        : $"{bytes / 1024d / 1024d:0.##} MiB";
    private long BeginBusy()
    {
        var generation = Interlocked.Increment(ref busyGeneration);
        IsBusy = true;
        return generation;
    }
    private void EndBusy(long generation)
    {
        if (generation == Volatile.Read(ref busyGeneration)) IsBusy = false;
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
