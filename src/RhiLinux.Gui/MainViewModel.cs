using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using RhiLinux.Core;
using RhiLinux.Mods;
using RhiLinux.Sources;
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

public enum MainSection
{
    Overview,
    Library,
    Updates,
    Diagnostics,
    Settings
}


public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
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
    private readonly Func<InstalledGame, CancellationToken, Task<DeploymentPlan>>? recommendedPlanBuilder;
    private readonly PlatformCapabilities platformCapabilities;
    private readonly IGameReadinessService readinessService;
    private readonly IGameOperationCoordinator operationCoordinator;
    private readonly IUserGameProfileStore profileStore;
    private readonly IActivityHistoryStore activityHistoryStore;
    private readonly IPortableProfileService portableProfiles;
    private readonly IBackupRecoveryCatalog backupRecoveryCatalog;
    private readonly IApplicationLog applicationLog;
    private readonly ITroubleshootingReportService troubleshootingReports;
    private readonly IOperationPreflightService operationPreflight;
    private readonly ILibraryCoordinator? ownedLibraryCoordinator;
    private readonly IDisposable? ownedNetworkClient;
    private readonly ConcurrentDictionary<string, GameReadinessState> readinessSummaries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> ownedBackgroundTasks = new();
    private readonly SemaphoreSlim preferencesSaveLock = new(1, 1);
    private readonly object backgroundTaskLock = new();
    private ApplicationState applicationState = new();
    private List<InstalledGame> games = [];
    private IReadOnlyList<InstalledGame> filteredGames = [];
    private GameSelectionSnapshot? activeSnapshot;
    private string searchText = string.Empty;
    private string libraryFilter = "All";
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
    private string scanMetricsText = string.Empty;
    private string protonSummary = string.Empty;
    private string hdrSummary = string.Empty;
    private string launchCompositionText = string.Empty;
    private string selectedNavigation = "Library";
    private IReadOnlyList<DiagnosticItemViewModel> diagnosticItems = [];
    private IReadOnlyList<ProviderStatusRowViewModel> providerStatuses = [];
    private string lastScanSummary = "No scan yet";
    private long providerRefreshGeneration;
    private IReadOnlyList<ComponentUpdateItemViewModel> componentUpdates = [];
    private readonly ConcurrentDictionary<string, IReadOnlyList<ComponentUpdateItemViewModel>> updatesByInstallId =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? libraryReadinessCancellation;
    private Task libraryReadinessTask = Task.CompletedTask;
    private Task providerRefreshTask = Task.CompletedTask;
    private Task pendingPreferencesSave = Task.CompletedTask;
    private long libraryReadinessGeneration;
    private long backgroundTaskGeneration;
    private bool isEvaluatingLibraryReadiness;
    private bool disposed;
    private IReadOnlyDictionary<GameInstallId, UserGameProfile> profiles =
        new Dictionary<GameInstallId, UserGameProfile>();
    private IReadOnlyList<ActivityHistoryEntry> recentOperations = [];
    private string troubleshootingReport = string.Empty;
    private IReadOnlyList<InterruptedOperationEntry> selectedRecoveries = [];

    public MainViewModel() : this(null) { }

    public MainViewModel(IReadOnlyList<string>? steamRoots)
    {
        paths = new XdgPaths();
        var discoveryService = new SteamDiscoveryService(new ExecutableDetector());
        var sourceProviders = MultiSourceLibraryService.CreateDefaultProviders(discoveryService);
        var multiSource = new MultiSourceLibraryService(
            sourceProviders,
            new GameAnalyzer(),
            new JsonSourceIndexStore(paths.SourceIndexFile));
        var initialContext = SourceRootDiscovery.CreateContext(customRoots: steamRoots);
        var coordinator = new LibraryCoordinator(
            multiSource,
            sourceProviders,
            initialContext,
            paths.LibraryDatabaseFile,
            paths.SourceIndexFile);
        ownedLibraryCoordinator = coordinator;
        discovery = new GameDiscoveryAdapter(coordinator, paths, steamRoots);
        var httpClient = MetadataHttp.CreateClient();
        ownedNetworkClient = httpClient;
        var stackStatusService = new StackStatusService(httpClient, paths);
        stackStatusProvider = new StackReportProviderAdapter(stackStatusService);
        var catalog = new GameProfileCatalog(paths);
        var detector = new ComponentDetector(catalog);
        statusProvider = new ComponentStatusProviderAdapter(detector);
        stateStore = new JsonStateStore(paths.StateFile);
        preferencesStore = new JsonUiPreferencesStore(Path.Combine(paths.AppConfigDirectory, "ui.json"));
        planner = new DeploymentPlanner();
        executor = new DeploymentExecutor();
        artifactCache = new ArtifactCacheService(paths);
        platformCapabilities = new LinuxPlatformCapabilityProvider().Detect();
        var officialPlanBuilder = new OfficialRecommendedDeploymentPlanBuilder(httpClient, paths, planner);
        readinessService = new GameReadinessService(
            stackStatusService,
            detector,
            new ProxyDiagnosticsService(catalog),
            new RecommendedSetupService(),
            new LaunchConfigurationService(),
            officialPlanBuilder);
        operationPreflight = new OperationPreflightService(paths);
        operationCoordinator = new GameOperationCoordinator(
            executor,
            readinessService,
            async (_, token) =>
            {
                await coordinator.RefreshAsync(
                    LibraryRefreshScope.Full("Deployment filesystem changes"), token).ConfigureAwait(false);
            },
            preflight: operationPreflight);
        profileStore = new JsonUserGameProfileStore(paths.GameProfilesFile);
        activityHistoryStore = new JsonActivityHistoryStore(paths.ActivityHistoryFile);
        portableProfiles = new PortableProfileService();
        backupRecoveryCatalog = new BackupRecoveryCatalog();
        applicationLog = new BoundedApplicationLog(paths.LogsDirectory);
        troubleshootingReports = new TroubleshootingReportService(applicationLog);
        ManualGameWizard = new(new ManualGameWizardService(paths.ManualGamesFile,
            detectComponents: (target, token) => detector.DetectAsync(target, token)));
    }

    public MainViewModel(
        IGameDiscovery discovery,
        IComponentStatusProvider statusProvider,
        IStateStore stateStore,
        IUiPreferencesStore preferencesStore,
        DeploymentPlanner? planner = null,
        IDeploymentExecutor? executor = null,
        IStackStatusProvider? stackStatusProvider = null,
        Func<InstalledGame, CancellationToken, Task<DeploymentPlan>>? recommendedPlanBuilder = null,
        IGameReadinessService? readinessService = null,
        IGameOperationCoordinator? operationCoordinator = null,
        IUserGameProfileStore? profileStore = null,
        IActivityHistoryStore? activityHistoryStore = null)
    {
        this.discovery = discovery;
        this.statusProvider = statusProvider;
        this.stateStore = stateStore;
        this.preferencesStore = preferencesStore;
        this.planner = planner ?? new DeploymentPlanner();
        this.executor = executor ?? new DeploymentExecutor();
        this.stackStatusProvider = stackStatusProvider;
        this.recommendedPlanBuilder = recommendedPlanBuilder;
        ownedNetworkClient = null;
        ownedLibraryCoordinator = null;
        paths = new XdgPaths();
        artifactCache = new ArtifactCacheService(paths);
        platformCapabilities = new LinuxPlatformCapabilityProvider().Detect();
        this.readinessService = readinessService ?? new GameReadinessService(
            recommendations: new RecommendedSetupService(),
            launchConfiguration: new LaunchConfigurationService(),
            planBuilder: recommendedPlanBuilder is null
                ? null
                : new DelegateRecommendedDeploymentPlanBuilder((game, _, token) => recommendedPlanBuilder(game, token)));
        this.operationCoordinator = operationCoordinator ?? new GameOperationCoordinator(this.executor, this.readinessService);
        operationPreflight = new OperationPreflightService(paths);
        this.profileStore = profileStore ?? new EmptyUserGameProfileStore();
        this.activityHistoryStore = activityHistoryStore ?? new EmptyActivityHistoryStore();
        portableProfiles = new PortableProfileService();
        backupRecoveryCatalog = new BackupRecoveryCatalog();
        applicationLog = new BoundedApplicationLog(paths.LogsDirectory);
        troubleshootingReports = new TroubleshootingReportService(applicationLog);
        ManualGameWizard = new(new ManualGameWizardService(paths.ManualGamesFile,
            detectComponents: (target, token) => new ComponentDetector().DetectAsync(target, token)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public OverviewViewModel OverviewPage { get; } = new();
    public LibraryViewModel LibraryPage { get; } = new();
    public UpdatesViewModel UpdatesPage { get; } = new();
    public DiagnosticsViewModel DiagnosticsPage { get; } = new();
    public SettingsViewModel SettingsPage { get; } = new();
    public GameDetailsViewModel GameDetailsPage { get; } = new();
    public ManualGameWizardViewModel ManualGameWizard { get; }
    public UiPreferences Preferences { get; private set; } = new();
    public IReadOnlyList<InstalledGame> Games => games;
    public IReadOnlyList<InstalledGame> FilteredGames { get => filteredGames; private set => Set(ref filteredGames, value); }
    public InstalledGame? SelectedGame => activeSnapshot?.Game;
    public IReadOnlyDictionary<GameInstallId, UserGameProfile> Profiles => profiles;
    public IReadOnlyList<ActivityHistoryEntry> RecentOperations
    {
        get => recentOperations;
        private set
        {
            if (!Set(ref recentOperations, value)) return;
            OnPropertyChanged(nameof(OverviewRecentOperationCount));
            OnPropertyChanged(nameof(ProfilesNeedingReviewCount));
        }
    }
    public int OverviewRecentOperationCount => RecentOperations.Count;
    public int ProfilesNeedingReviewCount => profiles.Values.Count(profile =>
        FindGame(profile.InstallId) is not { } game ||
        UserGameProfileValidator.Validate(game, profile).State == UserGameProfileState.NeedsReview);
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

    public string LibraryFilter
    {
        get => libraryFilter;
        set
        {
            if (!Set(ref libraryFilter, value ?? "All")) return;
            Preferences.LibraryFilter = libraryFilter;
            ApplyFilter();
        }
    }

    public string SelectedNavigation
    {
        get => selectedNavigation;
        set
        {
            var next = NormalizeNavigation(value);
            if (!Set(ref selectedNavigation, next)) return;
            OnPropertyChanged(nameof(SelectedSection));
            OnPropertyChanged(nameof(ShowOverviewPage));
            OnPropertyChanged(nameof(ShowLibraryPage));
            OnPropertyChanged(nameof(ShowUpdatesPage));
            OnPropertyChanged(nameof(ShowDiagnosticsPage));
            OnPropertyChanged(nameof(ShowSettingsPage));
            RaiseStateProperties();
            RaiseUpdatesPageProperties();
        }
    }

    public MainSection SelectedSection => SelectedNavigation switch
    {
        "Overview" => MainSection.Overview,
        "Updates" => MainSection.Updates,
        "Diagnostics" => MainSection.Diagnostics,
        "Settings" => MainSection.Settings,
        _ => MainSection.Library
    };

    public bool ShowOverviewPage => SelectedSection == MainSection.Overview;
    public bool ShowLibraryPage => SelectedSection == MainSection.Library;
    public bool ShowUpdatesPage => SelectedSection == MainSection.Updates;
    public bool ShowDiagnosticsPage => SelectedSection == MainSection.Diagnostics;
    public bool ShowSettingsPage => SelectedSection == MainSection.Settings;
    public IReadOnlyList<string> NavigationItems { get; } = ["Overview", "Library", "Updates", "Diagnostics", "Settings"];
    public IReadOnlyList<string> LibraryFilters { get; } =
    [
        "All", "Steam", "Epic", "GOG", "Amazon", "Heroic", "Legendary", "Lutris", "Bottles", "Minigalaxy", "Manual",
        "Windows", "Native/unsupported", "Needs executable confirmation",
        "Ready", "Needs attention", "Recovery required",
        "Not configured", "Anti-cheat", "Unsupported", "Unreal", "Unity", "RE Engine", "Installing"
    ];
    public string ScanMetricsText
    {
        get => scanMetricsText;
        private set
        {
            if (!Set(ref scanMetricsText, value)) return;
            OnPropertyChanged(nameof(HasScanMetrics));
        }
    }
    public bool HasScanMetrics => ScanMetricsText.Length > 0;
    public string SourceDiagnosticsText { get; private set; } = string.Empty;
    public IReadOnlyList<DiagnosticItemViewModel> DiagnosticItems
    {
        get => diagnosticItems;
        private set
        {
            diagnosticItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDiagnosticItems));
            OnPropertyChanged(nameof(DiagnosticWarningCount));
            OnPropertyChanged(nameof(OverviewDiagnosticSummary));
        }
    }
    public IReadOnlyList<ProviderStatusRowViewModel> ProviderStatuses
    {
        get => providerStatuses;
        private set
        {
            providerStatuses = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProviderStatuses));
        }
    }
    public bool HasDiagnosticItems => DiagnosticItems.Count > 0;
    public bool HasProviderStatuses => ProviderStatuses.Count > 0;
    public int DiagnosticWarningCount =>
        DiagnosticItems.Count(item => item.Severity is "Warning" or "Error");
    public string LastScanSummary
    {
        get => lastScanSummary;
        private set => Set(ref lastScanSummary, value);
    }
    public string ProtonSummary { get => protonSummary; private set => Set(ref protonSummary, value); }
    public string HdrSummary { get => hdrSummary; private set => Set(ref hdrSummary, value); }
    public string LaunchCompositionText { get => launchCompositionText; private set => Set(ref launchCompositionText, value); }
    public PlatformCapabilities PlatformCapabilities => platformCapabilities;
    public ReadinessPresentation Readiness { get; } = new();
    public int OverviewGameCount => games.Count;
    public int OverviewWindowsCount => games.Count(game =>
        game.Platform == GameBinaryPlatform.Windows && game.IsActionable && !game.IsNativeLinux);
    public int OverviewAntiCheatCount => games.Count(game => game.RequiresConfirmation);
    public int OverviewUnsupportedCount => games.Count(game => game.IsNativeLinux || game.Executable is null || !game.IsActionable);
    public int OverviewInstallingCount => games.Count(game => game.InstallState == SteamInstallState.Installing);
    public int OverviewEnabledSourceCount => CountEnabledSources();
    public int OverviewReadyCount => CountReadiness(GameReadinessState.Ready, GameReadinessState.ReadyWithWarnings);
    public int OverviewNeedsAttentionCount => CountReadiness(
        GameReadinessState.NeedsConfiguration, GameReadinessState.NeedsUserSelection, GameReadinessState.Error);
    public int OverviewUnsupportedReadinessCount => CountReadiness(
        GameReadinessState.Unsupported, GameReadinessState.Unavailable);
    public int OverviewReadinessPendingCount => Math.Max(0, games.Count - readinessSummaries.Count);
    public int OverviewUpdateCount => componentUpdates.Count(item => item.CanReview);
    public int OverviewRecoveryCount =>
        games.Count(game => DeploymentRecoveryProbe.Probe(game.GameRoot).HasInterruptedTransaction);
    public string OverviewSummary =>
        $"{OverviewGameCount} games · {OverviewReadyCount} ready · {OverviewNeedsAttentionCount} need attention" +
        (OverviewReadinessPendingCount > 0 ? $" · {OverviewReadinessPendingCount} evaluating" : string.Empty);
    public string OverviewAttentionSummary =>
        OverviewNeedsAttentionCount == 0 && OverviewUnsupportedReadinessCount == 0 && OverviewRecoveryCount == 0
            ? "No games currently need attention."
            : $"{OverviewNeedsAttentionCount} need attention · {OverviewUnsupportedReadinessCount} unsupported · {OverviewRecoveryCount} recoveries";
    public string OverviewSourceSummary =>
        $"{OverviewEnabledSourceCount} enabled · {DiagnosticWarningCount} provider warnings · {LastScanSummary}";
    public string OverviewDiagnosticSummary =>
        HasDiagnosticItems
            ? $"{DiagnosticItems.Count} recent diagnostics ({DiagnosticWarningCount} warnings/errors)."
            : "No recent source diagnostics.";
    public string OverviewUpdateSummary =>
        OverviewUpdateCount > 0
            ? $"{OverviewUpdateCount} component update{(OverviewUpdateCount == 1 ? "" : "s")} available."
            : IsCheckingUpdates
                ? "Checking official component sources…"
                : HasUpdates
                    ? UpdateStatusText
                    : "No pending component updates are known.";
    public IReadOnlyList<ComponentUpdateItemViewModel> ComponentUpdates
    {
        get => componentUpdates;
        private set
        {
            componentUpdates = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasComponentUpdates));
            OnPropertyChanged(nameof(OverviewUpdateCount));
            OnPropertyChanged(nameof(OverviewUpdateSummary));
            RaiseUpdatesPageProperties();
        }
    }
    public bool HasComponentUpdates => ComponentUpdates.Count > 0;
    public bool IsEvaluatingLibraryReadiness
    {
        get => isEvaluatingLibraryReadiness;
        private set
        {
            if (!Set(ref isEvaluatingLibraryReadiness, value)) return;
            OnPropertyChanged(nameof(OverviewSummary));
        }
    }
    public bool UpdatesHasAvailable => HasComponentUpdates || HasUpdates;
    public bool UpdatesIsLoading => IsCheckingUpdates;
    public bool UpdatesIsOffline => !IsCheckingUpdates && UpdateState == UpdateCheckState.Offline;
    public bool UpdatesIsUnavailable =>
        !IsCheckingUpdates &&
        !HasComponentUpdates &&
        !HasUpdates &&
        UpdateState == UpdateCheckState.UnableToCheck &&
        HasSelection;
    public bool UpdatesIsEmpty =>
        !IsCheckingUpdates &&
        !HasComponentUpdates &&
        !HasUpdates &&
        !UpdatesIsOffline &&
        !UpdatesIsUnavailable;
    public IReadOnlyList<ComponentCardViewModel> OutdatedComponents =>
        ComponentCards.Where(card => card.Health == ComponentHealth.Outdated).ToArray();

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
            OnPropertyChanged(nameof(CanRunPrimarySetup));
            OnPropertyChanged(nameof(CanCheckForUpdates));
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(CanManageCache));
        }
    }
    public bool CanInteract => !IsBusy;
    public bool CanInstallRecommendedStack => activeSnapshot?.CanInstallRecommendedStack == true && CanInteract;
    public bool CanRunPrimarySetup => CanInstallRecommendedStack &&
        PrimaryAction is PrimaryActionKind.Install or PrimaryActionKind.Update or PrimaryActionKind.Repair;
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
        PrimaryActionKind.Repair => "Repair",
        PrimaryActionKind.Remove => "Remove all managed components",
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
    public bool HasActiveWriteOperation => operationCoordinator.IsBusy;
    public string TroubleshootingReport
    {
        get => troubleshootingReport;
        private set
        {
            if (!Set(ref troubleshootingReport, value)) return;
            OnPropertyChanged(nameof(HasTroubleshootingReport));
        }
    }
    public bool HasTroubleshootingReport => TroubleshootingReport.Length > 0;
    public Task<OperationPreflightResult> CheckOperationPreflightAsync(
        DeploymentPlan plan,
        CancellationToken cancellationToken = default) =>
        operationPreflight.CheckAsync(plan, cancellationToken);
    public IReadOnlyList<InterruptedOperationEntry> SelectedRecoveries
    {
        get => selectedRecoveries;
        private set
        {
            if (!Set(ref selectedRecoveries, value)) return;
            OnPropertyChanged(nameof(HasRecoveryRequired));
            OnPropertyChanged(nameof(RecoveryRequiredMessage));
        }
    }
    public bool HasRecoveryRequired => SelectedRecoveries.Count > 0;
    public string RecoveryRequiredMessage => SelectedRecoveries.FirstOrDefault() is { } recovery
        ? $"An earlier install for {recovery.GameName} did not finish. The next install will automatically recover its incomplete changes before applying the new setup."
        : string.Empty;
    public bool HasFilteredGames => FilteredGames.Count > 0;
    public string FilteredGameCount => FilteredGames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool HasSelection => SelectedGame is not null;
    public bool IsSelectionLoading => activeSnapshot?.IsLoading == true;
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
            SchedulePreferencesSave();
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
            SchedulePreferencesSave();
        }
    }

    public bool ScanAllSources
    {
        get => Preferences.ScanAllSources;
        set
        {
            if (Preferences.ScanAllSources == value) return;
            Preferences.ScanAllSources = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverviewEnabledSourceCount));
            OnPropertyChanged(nameof(OverviewSummary));
            OnPropertyChanged(nameof(OverviewSourceSummary));
            ScheduleProviderRefresh();
        }
    }

    public bool EnableHeroic
    {
        get => Preferences.EnableHeroic;
        set
        {
            if (Preferences.EnableHeroic == value) return;
            Preferences.EnableHeroic = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverviewEnabledSourceCount));
            OnPropertyChanged(nameof(OverviewSummary));
            OnPropertyChanged(nameof(OverviewSourceSummary));
            ScheduleProviderRefresh();
        }
    }

    public bool EnableLegendary
    {
        get => Preferences.EnableLegendary;
        set
        {
            if (Preferences.EnableLegendary == value) return;
            Preferences.EnableLegendary = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverviewEnabledSourceCount));
            OnPropertyChanged(nameof(OverviewSummary));
            OnPropertyChanged(nameof(OverviewSourceSummary));
            ScheduleProviderRefresh();
        }
    }

    public bool EnableLutris
    {
        get => Preferences.EnableLutris;
        set
        {
            if (Preferences.EnableLutris == value) return;
            Preferences.EnableLutris = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverviewEnabledSourceCount));
            OnPropertyChanged(nameof(OverviewSummary));
            OnPropertyChanged(nameof(OverviewSourceSummary));
            ScheduleProviderRefresh();
        }
    }

    public bool EnableBottles
    {
        get => Preferences.EnableBottles;
        set
        {
            if (Preferences.EnableBottles == value) return;
            Preferences.EnableBottles = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverviewEnabledSourceCount));
            OnPropertyChanged(nameof(OverviewSummary));
            OnPropertyChanged(nameof(OverviewSourceSummary));
            ScheduleProviderRefresh();
        }
    }

    public bool EnableMinigalaxy
    {
        get => Preferences.EnableMinigalaxy;
        set
        {
            if (Preferences.EnableMinigalaxy == value) return;
            Preferences.EnableMinigalaxy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverviewEnabledSourceCount));
            OnPropertyChanged(nameof(OverviewSummary));
            OnPropertyChanged(nameof(OverviewSourceSummary));
            ScheduleProviderRefresh();
        }
    }

    public bool AutomaticallyEvaluateReadiness
    {
        get => Preferences.AutomaticallyEvaluateReadiness;
        set
        {
            if (Preferences.AutomaticallyEvaluateReadiness == value) return;
            Preferences.AutomaticallyEvaluateReadiness = value;
            OnPropertyChanged();
            SchedulePreferencesSave();
        }
    }

    public bool ShowUnsupportedNativeGames
    {
        get => Preferences.ShowUnsupportedNativeGames;
        set
        {
            if (Preferences.ShowUnsupportedNativeGames == value) return;
            Preferences.ShowUnsupportedNativeGames = value;
            OnPropertyChanged();
            ApplyFilter();
            SchedulePreferencesSave();
        }
    }

    public bool WarnBeforeAntiCheatDeployments
    {
        get => Preferences.WarnBeforeAntiCheatDeployments;
        set
        {
            if (Preferences.WarnBeforeAntiCheatDeployments == value) return;
            Preferences.WarnBeforeAntiCheatDeployments = value;
            OnPropertyChanged();
            SchedulePreferencesSave();
            if (SelectedGame is not null) StartBackground(() => RefreshReadinessAsync(true));
        }
    }

    public bool PreferExistingManagedVersions
    {
        get => Preferences.PreferExistingManagedVersions;
        set
        {
            if (Preferences.PreferExistingManagedVersions == value) return;
            Preferences.PreferExistingManagedVersions = value;
            OnPropertyChanged();
            SchedulePreferencesSave();
            if (SelectedGame is not null) StartBackground(() => RefreshReadinessAsync(true));
        }
    }

    public bool RefreshArtifactMetadataOnStartup
    {
        get => Preferences.RefreshArtifactMetadataOnStartup;
        set
        {
            if (Preferences.RefreshArtifactMetadataOnStartup == value) return;
            Preferences.RefreshArtifactMetadataOnStartup = value;
            OnPropertyChanged();
            SchedulePreferencesSave();
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
            ScheduleProviderRefresh();
        }
    }

    public string HeroicCustomRoot { get => CustomSourceRoot("heroic"); set => SetCustomSourceRoot("heroic", value); }
    public string LegendaryCustomRoot { get => CustomSourceRoot("legendary"); set => SetCustomSourceRoot("legendary", value); }
    public string LutrisCustomRoot { get => CustomSourceRoot("lutris"); set => SetCustomSourceRoot("lutris", value); }
    public string BottlesCustomRoot { get => CustomSourceRoot("bottles"); set => SetCustomSourceRoot("bottles", value); }
    public string MinigalaxyCustomRoot { get => CustomSourceRoot("minigalaxy"); set => SetCustomSourceRoot("minigalaxy", value); }
    public string SelectionProgress => activeSnapshot?.Progress ?? string.Empty;
    public ExecutionResult? PreviousOperationResult => activeSnapshot?.PreviousOperationResult;
    public bool ShowWelcome => ShowLibraryPage && CurrentState == UiState.Empty;
    public bool ShowNoGames => ShowLibraryPage && CurrentState == UiState.NoGames;
    public bool ShowNoMatches => ShowLibraryPage && HasGames && !HasFilteredGames && !IsBusy;
    public bool ShowGame => ShowLibraryPage && HasSelection && CurrentState is not UiState.Empty and not UiState.NoGames;
    public bool ShowGameLoading => ShowGame && IsSelectionLoading;
    public bool HasWarnings => SelectedGame?.RequiresConfirmation == true || ComponentCards.Any(x => x.Health == ComponentHealth.Conflicting);
    public string WarningText => SelectedGame?.RequiresConfirmation == true
        ? "Anti-cheat files were detected. Deployment requires additional confirmation and may be unsupported."
        : ComponentCards.Any(x => x.Health == ComponentHealth.Conflicting)
            ? "An unknown compatibility file is blocking installation. Advanced details show the occupied filename."
            : string.Empty;
    public string SelectedGameTitle => SelectedGame?.Name ?? "No game selected";
    public string SelectedGameSubtitle => SelectedGame is null ? string.Empty :
        SelectedGame.IdentitySummary +
        (SelectedGame.ExternalId is { Length: > 0 } external && SelectedGame.Store != GameStore.Steam
            ? $"  ·  {external}"
            : SelectedGame.Store == GameStore.Steam
                ? $"  ·  AppID {SelectedGame.AppId}"
                : string.Empty) +
        (SelectedGame.InstallState == SteamInstallState.Installing ? "  ·  Installing" : string.Empty);
    public string ExecutableDisplay => SelectedGame?.Executable is null ? "Not detected" : Path.GetFileName(SelectedGame.Executable);
    public string ExecutablePath => SelectedGame?.Executable ?? string.Empty;
    public string DeploymentDisplay => SelectedGame is null ? string.Empty : ShortenPath(SelectedGame.DeploymentDirectory ?? SelectedGame.GameRoot);
    public string DeploymentPath => SelectedGame?.DeploymentDirectory ?? string.Empty;
    public string ProtonPrefixDisplay => SelectedGame is null ? string.Empty :
        SelectedGame.HasProtonPrefix ? ShortenPath(SelectedGame.ProtonPrefix) : "Proton prefix not created yet";
    public string ProtonPrefixPath => SelectedGame?.ProtonPrefix ?? string.Empty;
    public string ConfidenceText => SelectedGame is null ? string.Empty : $"{SelectedGame.Confidence} confidence";
    public string ArchitectureDisplay => SelectedGame?.CandidateList.FirstOrDefault(x =>
            SelectedGame.Executable is not null &&
            Path.GetFullPath(x.Path).Equals(Path.GetFullPath(SelectedGame.Executable), StringComparison.Ordinal))?.Architecture
            .ToString() ?? SelectedGame?.CandidateList.FirstOrDefault()?.Architecture.ToString() ?? "Unknown";
    public string EngineDisplay => SelectedGame?.Engine.ToString() ?? "Unknown";
    public string SelectionReason => SelectedGame?.SelectionReason ?? string.Empty;
    public IReadOnlyList<ExecutableCandidateDisplay> Candidates => SelectedGame is not { } game ? [] :
        game.CandidateList
            .Select(candidate => new ExecutableCandidateDisplay(
                Path.GetFileName(candidate.Path),
                Path.GetRelativePath(game.GameRoot, candidate.Path),
                candidate.Architecture.ToString(),
                candidate.Confidence == DetectionConfidence.None ? "Rejected" : candidate.Confidence.ToString(),
                candidate.Score,
                candidate.Reasons.FirstOrDefault() ?? "Executable discovered by game analysis",
                candidate.Path,
                game.Executable is not null && PathsEqual(candidate.Path, game.Executable),
                candidate.Confidence == DetectionConfidence.None || candidate.Score < 0))
            .OrderByDescending(candidate => candidate.IsCurrent)
            .ThenByDescending(candidate => candidate.IsRejected ? -1 : ConfidenceRank(candidate.Confidence))
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.RelativePath.Length)
            .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    public string LaunchOption => activeSnapshot?.RequiredLaunchOption ?? string.Empty;
    public bool HasLaunchOption => LaunchOption.Length > 0;
    public string LaunchOptionDescription => LaunchOption.Contains(SteamLaunchOptionService.DxvkHdr, StringComparison.Ordinal)
        ? LaunchOption.Contains(SteamLaunchOptionService.ProtonEnableWayland, StringComparison.Ordinal)
            ? "RenoDX HDR and the tested Proton Wayland path are included for this game."
            : "RenoDX HDR is included. Proton Wayland stays disabled because it is not a tested requirement for this game."
        : "This value loads the installed compatibility proxy without adding optional Proton features.";
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
            SchedulePreferencesSave();
        }
    }

    public void ResetSettingsToDefaults()
    {
        Preferences.CacheLimitMiB = 5 * 1024;
        Preferences.ReduceMotion = false;
        Preferences.CheckForUpdatesAutomatically = true;
        Preferences.AdditionalSteamLibrary = string.Empty;
        Preferences.CustomSourceRoots.Clear();
        Preferences.Theme = "System";
        Preferences.ScanAllSources = true;
        Preferences.EnableHeroic = true;
        Preferences.EnableLegendary = true;
        Preferences.EnableLutris = true;
        Preferences.EnableBottles = true;
        Preferences.EnableMinigalaxy = true;
        Preferences.AutomaticallyEvaluateReadiness = true;
        Preferences.ShowUnsupportedNativeGames = true;
        Preferences.WarnBeforeAntiCheatDeployments = true;
        Preferences.PreferExistingManagedVersions = true;
        Preferences.RefreshArtifactMetadataOnStartup = true;
        OnPropertyChanged(nameof(CacheLimitGiB));
        OnPropertyChanged(nameof(ReduceMotion));
        OnPropertyChanged(nameof(UseMotion));
        OnPropertyChanged(nameof(CheckForUpdatesAutomatically));
        OnPropertyChanged(nameof(AdditionalSteamLibrary));
        OnPropertyChanged(nameof(HeroicCustomRoot));
        OnPropertyChanged(nameof(LegendaryCustomRoot));
        OnPropertyChanged(nameof(LutrisCustomRoot));
        OnPropertyChanged(nameof(BottlesCustomRoot));
        OnPropertyChanged(nameof(MinigalaxyCustomRoot));
        OnPropertyChanged(nameof(ScanAllSources));
        OnPropertyChanged(nameof(EnableHeroic));
        OnPropertyChanged(nameof(EnableLegendary));
        OnPropertyChanged(nameof(EnableLutris));
        OnPropertyChanged(nameof(EnableBottles));
        OnPropertyChanged(nameof(EnableMinigalaxy));
        OnPropertyChanged(nameof(AutomaticallyEvaluateReadiness));
        OnPropertyChanged(nameof(ShowUnsupportedNativeGames));
        OnPropertyChanged(nameof(WarnBeforeAntiCheatDeployments));
        OnPropertyChanged(nameof(PreferExistingManagedVersions));
        OnPropertyChanged(nameof(RefreshArtifactMetadataOnStartup));
        OnPropertyChanged(nameof(OverviewEnabledSourceCount));
        OnPropertyChanged(nameof(OverviewSummary));
        OnPropertyChanged(nameof(OverviewSourceSummary));
        ScheduleProviderRefresh();
    }

    public void Navigate(MainSection section) =>
        SelectedNavigation = section switch
        {
            MainSection.Overview => "Overview",
            MainSection.Updates => "Updates",
            MainSection.Diagnostics => "Diagnostics",
            MainSection.Settings => "Settings",
            _ => "Library"
        };

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
            OnPropertyChanged(nameof(HeroicCustomRoot));
            OnPropertyChanged(nameof(LegendaryCustomRoot));
            OnPropertyChanged(nameof(LutrisCustomRoot));
            OnPropertyChanged(nameof(BottlesCustomRoot));
            OnPropertyChanged(nameof(MinigalaxyCustomRoot));
            OnPropertyChanged(nameof(ScanAllSources));
            OnPropertyChanged(nameof(EnableHeroic));
            OnPropertyChanged(nameof(EnableLegendary));
            OnPropertyChanged(nameof(EnableLutris));
            OnPropertyChanged(nameof(EnableBottles));
            OnPropertyChanged(nameof(EnableMinigalaxy));
            applicationState = await stateStore.LoadAsync(cancellationToken);
            profiles = await profileStore.LoadAsync(cancellationToken);
            RecentOperations = await activityHistoryStore.LoadAsync(cancellationToken);
            OnPropertyChanged(nameof(Profiles));
            OnPropertyChanged(nameof(ProfilesNeedingReviewCount));
            libraryFilter = string.IsNullOrWhiteSpace(Preferences.LibraryFilter) ? "All" : Preferences.LibraryFilter;
            OnPropertyChanged(nameof(LibraryFilter));
            if (applicationState.DiscoveredGames.Count > 0)
            {
                games = applicationState.DiscoveredGames.Select(InstalledGame.FromPersistedGameEntry).ToList();
                ApplyFilter();
                RaiseOverviewProperties();
                CurrentState = games.Count == 0 ? UiState.NoGames : UiState.Ready;
                GlobalStatus = $"{games.Count} games from cache · refreshing…";
                var cached = ResolvePreferredGame(games, Preferences) ?? FilteredGames.FirstOrDefault();
                if (cached is not null)
                    await SelectAsync(cached, cancellationToken);
            }
            else
                CurrentState = UiState.Empty;
            await RefreshAsync(cancellationToken);
            await applicationLog.WriteAsync(new(DateTimeOffset.UtcNow, ApplicationLogLevel.Information,
                "startup", $"Application initialized with {games.Count} cached or discovered games."), cancellationToken);
        }
        catch (OperationCanceledException) { GlobalStatus = "Scan cancelled"; CurrentState = UiState.Empty; }
        catch (Exception exception) { SetError("Could not initialize", exception); }
        finally { EndBusy(busy); }
        try
        {
            await RefreshCacheStatisticsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CacheSizeText = "Unavailable";
            CacheCleanupText = "Unavailable";
            ErrorMessage ??= $"Could not read cache statistics: {exception.Message}";
        }
    }

    public async Task ExportProfilesAsync(
        string path,
        IReadOnlyCollection<GameInstallId>? selected = null,
        CancellationToken cancellationToken = default)
    {
        var ids = selected is { Count: > 0 } ? selected.ToHashSet() : null;
        var values = profiles.Values.Where(profile => ids is null || ids.Contains(profile.InstallId)).ToArray();
        await portableProfiles.ExportAsync(path, values, games.ToDictionary(game => game.InstallId), cancellationToken);
        GlobalStatus = $"Exported {values.Length} profile{(values.Length == 1 ? string.Empty : "s")}";
    }

    public Task<ProfileImportPreview> PreviewProfileImportAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        portableProfiles.PreviewImportAsync(path, games, cancellationToken);

    public async Task ApplyProfileImportAsync(
        ProfileImportPreview preview,
        IReadOnlyCollection<ProfileImportApproval> approvals,
        CancellationToken cancellationToken = default)
    {
        var imported = await portableProfiles.ApplyImportAsync(
            preview, approvals, profiles, profileStore, cancellationToken);
        profiles = imported.ToDictionary(profile => profile.InstallId);
        OnPropertyChanged(nameof(Profiles));
        OnPropertyChanged(nameof(ProfilesNeedingReviewCount));
        readinessService.InvalidateAll();
        GlobalStatus = "Profiles imported";
    }

    public async Task<string> CreateTroubleshootingReportAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = (discovery as GameDiscoveryAdapter)?.LastSnapshot;
        var providers = snapshot?.Providers ?? [];
        var diagnostics = (discovery as GameDiscoveryAdapter)?.LastSourceDiagnostics ?? [];
        var lastOperation = RecentOperations.FirstOrDefault();
        var recoveryCount = games.Count(game => DeploymentRecoveryProbe.Probe(game.GameRoot).HasInterruptedTransaction);
        TroubleshootingReport = await troubleshootingReports.CreateAsync(new(
            paths,
            providers,
            games.Count,
            snapshot?.Metrics ?? LibraryMetrics.Empty,
            SqliteLibraryIndex.SchemaVersion,
            File.Exists(paths.LibraryDatabaseFile) ? "Available" : "Not created",
            Preferences.WatchSteamLibraries ? "Enabled" : "Disabled",
            lastOperation is null ? "No operations yet" : $"{lastOperation.Outcome}: {lastOperation.Action}",
            recoveryCount,
            diagnostics), cancellationToken);
        return TroubleshootingReport;
    }

    public Task SaveTroubleshootingReportAsync(
        string path,
        string report,
        CancellationToken cancellationToken = default) =>
        troubleshootingReports.SaveAsync(path, report, cancellationToken);

    public async Task CompleteOnboardingAsync(
        IReadOnlyDictionary<string, bool> providerPreferences,
        CancellationToken cancellationToken = default)
    {
        Preferences.EnableHeroic = providerPreferences.GetValueOrDefault("heroic", Preferences.EnableHeroic);
        Preferences.EnableLegendary = providerPreferences.GetValueOrDefault("legendary", Preferences.EnableLegendary);
        Preferences.EnableLutris = providerPreferences.GetValueOrDefault("lutris", Preferences.EnableLutris);
        Preferences.EnableBottles = providerPreferences.GetValueOrDefault("bottles", Preferences.EnableBottles);
        Preferences.EnableMinigalaxy = providerPreferences.GetValueOrDefault("minigalaxy", Preferences.EnableMinigalaxy);
        Preferences.OnboardingCompleted = true;
        await SavePreferencesAsync(cancellationToken);
    }

    public async Task RunSetupAgainAsync(CancellationToken cancellationToken = default)
    {
        Preferences.OnboardingCompleted = false;
        await SavePreferencesAsync(cancellationToken);
    }

    public async Task VerifyCacheAsync(CancellationToken cancellationToken = default)
    {
        IsCacheBusy = true;
        ErrorMessage = null;
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

    public async Task RefreshRenoDxCatalogAsync(CancellationToken cancellationToken = default)
    {
        IsCacheBusy = true;
        ErrorMessage = null;
        GlobalStatus = "Refreshing RenoDX catalog…";
        try
        {
            using var http = MetadataHttp.CreateClient();
            var catalog = await new RenoDxWikiClient(http, paths).GetAsync(true, cancellationToken, forceRefresh: true);
            if (catalog.State == RenoDxWikiFetchState.UnableToCheck && catalog.Entries.Count == 0)
            {
                ErrorMessage = catalog.Warning ?? "RenoDX catalog unavailable";
                GlobalStatus = "RenoDX catalog refresh failed";
                return;
            }
            if (SelectedGame is not null)
                await SelectAsync(SelectedGame, cancellationToken);
            GlobalStatus = catalog.Changed
                ? $"RenoDX catalog updated ({catalog.Entries.Count} entries)"
                : $"RenoDX catalog refreshed ({catalog.Entries.Count} entries)";
        }
        finally { IsCacheBusy = false; }
    }

    public async Task ClearUnusedCacheAsync(CancellationToken cancellationToken = default)
    {
        IsCacheBusy = true;
        ErrorMessage = null;
        GlobalStatus = "Clearing unused downloads…";
        try
        {
            await RefreshArtifactReferencesAsync(games, cancellationToken);
            await stateStore.SaveAsync(applicationState, cancellationToken);
            var references = applicationState.ArtifactReferencesByInstallId.Values.SelectMany(x => x)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = await artifactCache.CleanupAsync(references,
                Preferences.CacheLimitMiB * 1024L * 1024L, cancellationToken);
            await RefreshCacheStatisticsAsync(cancellationToken);
            GlobalStatus = $"Freed {FormatBytes(result.BytesRemoved)}";
        }
        finally { IsCacheBusy = false; }
    }

    public async Task RebuildLibraryCacheAsync(bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new InvalidOperationException("Rebuilding the library cache requires confirmation.");
        if (discovery is not GameDiscoveryAdapter adapter)
            throw new InvalidOperationException("The incremental library cache is not configured.");
        IsCacheBusy = true;
        try
        {
            await adapter.RebuildCacheAsync(cancellationToken);
            GlobalStatus = "Library cache rebuilt · game files, profiles, backups, and ownership were preserved";
            await RefreshAsync(cancellationToken);
        }
        finally
        {
            IsCacheBusy = false;
        }
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
            adapter.CustomRootsByProvider = Preferences.CustomSourceRoots
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key,
                    pair => (IReadOnlyList<string>)[pair.Value], StringComparer.OrdinalIgnoreCase);
            adapter.UseAllSources = Preferences.ScanAllSources;
            adapter.ForceFullAnalysis = false;
            adapter.WatchSources = Preferences.WatchSteamLibraries;
            var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam" };
            if (Preferences.ScanAllSources)
            {
                if (Preferences.EnableHeroic) enabled.Add("heroic");
                if (Preferences.EnableLegendary) enabled.Add("legendary");
                if (Preferences.EnableLutris) enabled.Add("lutris");
                if (Preferences.EnableBottles) enabled.Add("bottles");
                if (Preferences.EnableMinigalaxy) enabled.Add("minigalaxy");
            }
            enabled.Add("manual");
            adapter.EnabledProviders = enabled;
        }
        var previousGameRows = games.ToArray();
        var previousGames = previousGameRows.Select(GameIdentity).ToHashSet();
        var selectedInstallId = SelectedGame?.EffectiveInstallId ?? Preferences.SelectedInstallId;
        var selectedAppId = SelectedGame?.SteamAppId ?? Preferences.SelectedAppId;
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
        GlobalStatus = Preferences.ScanAllSources ? "Scanning game sources…" : "Scanning Steam libraries…";
        try
        {
            var result = await discovery.ScanAsync(applicationState.Overrides, cancellationToken);
            await RefreshArtifactReferencesAsync(
                applicationState.DiscoveredGames.Select(InstalledGame.FromPersistedGameEntry).Concat(result.Games),
                cancellationToken);
            LibraryCollectionDiff.Apply(games, result.Games);
            await RelinkMovedProfilesAsync(previousGameRows, cancellationToken);
            var existingIds = games.Select(game => game.EffectiveInstallId).ToHashSet(StringComparer.Ordinal);
            foreach (var installId in updatesByInstallId.Keys.Where(id => !existingIds.Contains(id)))
                updatesByInstallId.TryRemove(installId, out _);
            RefreshComponentUpdates();
            applicationState.DiscoveredGames = games.Select(PersistedGameEntry.FromInstalledGame).ToList();
            applicationState.LastScanUtc = DateTimeOffset.UtcNow;
            await stateStore.SaveAsync(applicationState, cancellationToken);
            OnPropertyChanged(nameof(Games));
            OnPropertyChanged(nameof(HasGames));
            RaiseOverviewProperties();
            ApplyFilter();
            var metrics = (discovery as GameDiscoveryAdapter)?.LastMetrics;
            var multi = (discovery as GameDiscoveryAdapter)?.LastMultiSourceResult;
            var librarySnapshot = (discovery as GameDiscoveryAdapter)?.LastSnapshot;
            if (librarySnapshot is not null)
            {
                var libraryMetrics = librarySnapshot.Metrics;
                ScanMetricsText =
                    $"Reason: {libraryMetrics.Reason} · " +
                    $"Providers: {string.Join(", ", libraryMetrics.ProvidersExecuted)} · " +
                    $"Documents parsed: {libraryMetrics.SourceDocumentsParsed} · " +
                    $"Games reconciled: {libraryMetrics.InstallationsReconciled} · " +
                    $"Games analyzed: {libraryMetrics.InstallationsAnalyzed} · " +
                    $"Duration: {libraryMetrics.TotalDuration.TotalMilliseconds:0} ms";
                ApplySourceSnapshot(librarySnapshot);
            }
            else if (multi is not null)
            {
                ScanMetricsText =
                    $"{multi.Games.Count} installs · gen {multi.ScanGeneration} · " +
                    string.Join(" · ", multi.TimingsMilliseconds.Select(entry => $"{entry.Key} {entry.Value:0} ms"));
                ApplySourcePanels(multi);
            }
            else if (metrics is not null)
            {
                ScanMetricsText =
                    $"{metrics.CacheHits} cache hits · {metrics.Analyzed} analyzed · {metrics.ElapsedMilliseconds} ms";
                LastScanSummary = $"Last scan {DateTimeOffset.Now:g}";
                ProviderStatuses = BuildSteamOnlyProviderStatuses(games.Count);
                DiagnosticItems = [];
                SourceDiagnosticsText = string.Empty;
                OnPropertyChanged(nameof(SourceDiagnosticsText));
            }
            else
            {
                LastScanSummary = applicationState.LastScanUtc is { } stamp
                    ? $"Last scan {stamp.ToLocalTime():g}"
                    : "No scan yet";
                ProviderStatuses = BuildConfiguredProviderStatuses();
            }
            var selectionChangedDuringScan = refreshSelectionGeneration != Volatile.Read(ref selectionGeneration);
            var targetInstallId = selectionChangedDuringScan
                ? SelectedGame?.EffectiveInstallId
                : selectedInstallId;
            var targetAppId = selectionChangedDuringScan
                ? SelectedGame?.SteamAppId
                : selectedAppId;
            var restored = !string.IsNullOrWhiteSpace(targetInstallId)
                ? games.FirstOrDefault(x => x.EffectiveInstallId == targetInstallId)
                : targetAppId.HasValue
                    ? games.FirstOrDefault(x => x.AppId == targetAppId.Value)
                    : null;
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
                    ? multi is { UsedCache: true }
                        ? $"{games.Count} games · unchanged sources reused"
                        : metrics is { CacheHits: > 0 }
                            ? $"{games.Count} games · incremental refresh ({metrics.CacheHits} cached)"
                            : $"{games.Count} games found · library is up to date"
                    : $"{games.Count} games found";
            StartLibraryReadinessEvaluation(cancellationToken);
        }
        catch (OperationCanceledException) { GlobalStatus = "Scan cancelled"; CurrentState = games.Count == 0 ? UiState.Empty : UiState.Ready; }
        catch (Exception exception)
        {
            SetError("Library scan failed", exception);
            await applicationLog.WriteAsync(new(DateTimeOffset.UtcNow, ApplicationLogLevel.Error,
                "library-refresh", "Library refresh failed.", exception.GetType().Name), CancellationToken.None);
        }
        finally { EndBusy(busy); ClearStaleSelectionLoading(); RaiseStateProperties(); }
    }

    private void ClearStaleSelectionLoading()
    {
        bool cleared;
        lock (selectionLock)
        {
            cleared = activeSnapshot is { IsLoading: true };
            if (cleared) activeSnapshot = activeSnapshot! with { IsLoading = false };
        }

        if (cleared) RaiseSelectionProperties();
    }

    private static string FormatSourceDiagnostics(MultiSourceScanResult multi)
    {
        var builder = new StringBuilder();
        foreach (var result in multi.ProviderResults.OrderBy(item => item.ProviderId, StringComparer.Ordinal))
        {
            builder.AppendLine(
                $"{result.ProviderId}: root={(result.Root.Exists ? "ok" : "missing")} " +
                $"games={result.Games.Count} malformed={result.MalformedRecords.Count} " +
                $"skipped={result.SkippedRecords.Count} cache={(result.FromCache ? "hit" : "miss")} " +
                $"duration={result.Duration.TotalMilliseconds:0}ms");
            if (!string.IsNullOrWhiteSpace(result.Root.SkipReason))
                builder.AppendLine($"  {result.Root.SkipReason}");
        }

        foreach (var diagnostic in multi.Diagnostics.Take(40))
            builder.AppendLine($"{diagnostic.Severity}: [{diagnostic.ProviderId}/{diagnostic.Code}] {diagnostic.Message}");
        return builder.ToString().TrimEnd();
    }

    private static string GameIdentity(InstalledGame game) =>
        $"{game.EffectiveInstallId}:{Path.GetFullPath(game.GameRoot).TrimEnd(Path.DirectorySeparatorChar)}";

    private static InstalledGame? ResolvePreferredGame(IReadOnlyList<InstalledGame> games, UiPreferences preferences)
    {
        if (!string.IsNullOrWhiteSpace(preferences.SelectedInstallId))
        {
            var byInstall = games.FirstOrDefault(game => game.EffectiveInstallId == preferences.SelectedInstallId);
            if (byInstall is not null) return byInstall;
        }
        if (preferences.SelectedAppId is { } appId)
        {
            var byApp = games.FirstOrDefault(game => game.AppId == appId);
            if (byApp is not null) return byApp;
        }
        return null;
    }

    private async Task RefreshArtifactReferencesAsync(
        IEnumerable<InstalledGame> knownGames,
        CancellationToken cancellationToken)
    {
        foreach (var game in knownGames.GroupBy(x => x.EffectiveInstallId).Select(group => group.Last()))
        {
            if (!Directory.Exists(game.GameRoot)) continue;
            try
            {
                var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
                if (manifest.Files.Count > 0)
                {
                    if (!string.IsNullOrWhiteSpace(manifest.InstallId) &&
                        !string.Equals(manifest.InstallId, game.EffectiveInstallId, StringComparison.Ordinal))
                        continue;
                    if (string.IsNullOrWhiteSpace(manifest.InstallId) &&
                        manifest.SteamAppId is { } manifestSteam &&
                        game.SteamAppId is { } gameSteam &&
                        manifestSteam != gameSteam)
                        continue;
                }
                applicationState.ArtifactReferencesByInstallId[game.EffectiveInstallId] = manifest.Files
                    .SelectMany(file => new[] { file.SourceBlobSha256, file.SourceBundleSha256 })
                    .Where(hash => !string.IsNullOrWhiteSpace(hash))
                    .Select(hash => hash!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or System.Text.Json.JsonException)
            {
            }
        }
    }

    public async Task SelectAsync(InstalledGame? game, CancellationToken cancellationToken = default)
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
        var gameChanged = previousSnapshot?.Game.EffectiveInstallId != game.EffectiveInstallId;
        Preferences.SelectedInstallId = game.EffectiveInstallId;
        Preferences.SelectedAppId = game.SteamAppId;
        if (gameChanged)
        {
            ComponentUpdates = [];
            Readiness.SetResult(null);
            OnPropertyChanged(nameof(Readiness));
        }
        ApplySnapshot(GameSelectionSnapshot.Loading(game, generation, stackStatusProvider is not null), UiState.Loading);
        SetSelectionStatus($"Detecting {game.Name}…", game.EffectiveInstallId, generation, selectionToken);
        SetSelectionError(null, game.EffectiveInstallId, generation);
        var timing = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var catalog = new GameProfileCatalog(paths);
            var target = game.ToDeploymentTarget();
            var statusTask = statusProvider.DetectAsync(game, selectionToken);
            var profileTask = catalog.MatchAsync(target, selectionToken);
            var proxyTask = new ProxyDiagnosticsService(catalog).DiagnoseAsync(target, selectionToken);
            var recoveryTask = backupRecoveryCatalog.ListInterruptedAsync(game, selectionToken);
            await Task.WhenAll(statusTask, profileTask, proxyTask, recoveryTask);
            if (!IsCurrentSelection(game.EffectiveInstallId, generation, selectionToken)) return;
            var localMs = timing.ElapsedMilliseconds;
            var statuses = await statusTask;
            var profile = await profileTask;
            var proxy = await proxyTask;
            SelectedRecoveries = await recoveryTask;
            var eligibility = OptiScalerEligibilityService.Evaluate(target, profile, proxy);
            var cards = CreateComponentCards(statuses, null, gameChanged ? null : previousSnapshot);
            var support = BuildSupportSummary(profile, proxy, null, eligibility, false);
            var proposedAction = DeterminePrimaryAction(statuses, false);
            if (proposedAction == PrimaryActionKind.None && support.CanInstall)
                proposedAction = PrimaryActionKind.Install;
            var launch = ResolveLaunchOption(game, statuses, proxy);
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
                launch,
                autoUpdates ? SelectionPhase.CheckingUpdates : SelectionPhase.Idle,
                $"Local detect {localMs} ms · compatibility from catalog/proxy · updates {(autoUpdates ? "background" : "skipped")}");
            if (!IsCurrentSelection(game.EffectiveInstallId, generation, selectionToken)) return;
            var state = cards.Any(x => x.Health == ComponentHealth.Conflicting) ? UiState.Conflict :
                game.RequiresConfirmation ? UiState.Unsupported : UiState.Ready;
            ApplySnapshot(snapshot, state, selectionToken);
            UpdateLinuxDiagnostics(game, launch);
            SetSelectionStatus(support.Title, game.EffectiveInstallId, generation, selectionToken);
            if (Preferences.AutomaticallyEvaluateReadiness)
                StartBackground(() => EvaluateReadinessForSelectionAsync(
                    game, generation, statuses, proxy, launch, selectionToken));
        }
        catch (OperationCanceledException) when (selectionToken.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            SetSelectionError($"Could not read this game: {exception.Message}", game.EffectiveInstallId, generation, selectionToken, UiState.Error);
        }
        if (!IsCurrentSelection(game.EffectiveInstallId, generation, selectionToken)) return;
        if (stackStatusProvider is not null && Preferences.CheckForUpdatesAutomatically)
            StartBackground(() => CheckForUpdatesAsync(true, selectionToken));
    }

    public Task RefreshReadinessAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null || activeSnapshot is null) return Task.CompletedTask;
        return EvaluateReadinessForSelectionAsync(
            SelectedGame,
            activeSnapshot.Generation,
            null,
            null,
            activeSnapshot.RequiredLaunchOption,
            cancellationToken,
            force);
    }

    public void OpenReadinessForInstall(string installId)
    {
        var game = games.FirstOrDefault(item => item.EffectiveInstallId == installId);
        if (game is null) return;
        Navigate(MainSection.Library);
        StartBackground(() => SelectAsync(game));
    }

    public void FilterLibraryForReadiness(string filter)
    {
        LibraryFilter = filter;
        Navigate(MainSection.Library);
    }

    public void AttachRecommendedPlanSummary(DeploymentPlan plan)
    {
        Readiness.AttachPlanSummary(DeploymentPlanSummary.From(plan));
        OnPropertyChanged(nameof(Readiness));
    }

    public async Task CheckForUpdatesAsync(bool automatic = false, CancellationToken cancellationToken = default)
    {
        if (activeSnapshot is not { } startingSnapshot || stackStatusProvider is null || startingSnapshot.IsCheckingUpdates) return;
        if (!automatic) ErrorMessage = null;
        var game = startingSnapshot.Game;
        var generation = startingSnapshot.Generation;
        CancellationToken selectionToken;
        lock (selectionLock) selectionToken = selectionCancellation?.Token ?? CancellationToken.None;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, selectionToken);
        linkedCancellation.CancelAfter(MetadataHttp.UpdateCheckBudget);
        var updateToken = linkedCancellation.Token;
        if (!IsCurrentSelection(game.EffectiveInstallId, generation, updateToken)) return;
        var updateStarted = System.Diagnostics.Stopwatch.StartNew();
        ApplySnapshot(startingSnapshot with
        {
            IsCheckingUpdates = true,
            UpdateState = UpdateCheckState.Checking,
            Phase = SelectionPhase.CheckingUpdates
        }, CurrentState, updateToken);
        if (!automatic) SetSelectionStatus("Checking for component updates…", game.EffectiveInstallId, generation, updateToken);
        try
        {
            var report = await stackStatusProvider.GetAsync(game, true, updateToken, forceRefresh: !automatic);
            if (!IsCurrentSelection(game.EffectiveInstallId, generation, updateToken)) return;
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
            var cards = CreateComponentCards(report.Components, report.ArtifactResolution.Components, activeSnapshot,
                report.ArtifactResolution.OfficialPageUrl, report.ArtifactResolution.CompatibilityNotes);
            var support = BuildSupportSummary(report.Profile, report.Proxy, report, report.OptiScalerEligibility, hasUpdates);
            var proposedAction = DeterminePrimaryAction(report.Components, hasUpdates);
            var primaryAction = proposedAction == PrimaryActionKind.Remove || CanPrepareAutomaticPlan(report, proposedAction) ||
                (proposedAction == PrimaryActionKind.Install && support.CanInstall)
                ? proposedAction
                : PrimaryActionKind.None;
            var launch = ResolveLaunchOption(game, report.Components, report.Proxy);
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
                launch,
                SelectionPhase.Idle,
                timing);
            if (!IsCurrentSelection(game.EffectiveInstallId, generation, updateToken)) return;
            if (!ApplySnapshot(completed, cards.Any(x => x.Health == ComponentHealth.Conflicting) ? UiState.Conflict :
                    game.RequiresConfirmation ? UiState.Unsupported : UiState.Ready, updateToken,
                    preserveOperationStatus: automatic)) return;
            if (!automatic || !HasActiveOperationStatus(game.EffectiveInstallId, generation, updateToken))
                SetSelectionStatus(automatic ? SupportTitle : UpdateStatusText, game.EffectiveInstallId, generation, updateToken);
        }
        catch (OperationCanceledException)
        {
            if (activeSnapshot is { } current &&
                current.Game.EffectiveInstallId == game.EffectiveInstallId &&
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
                    SetSelectionStatus("Could not reach update sources", game.EffectiveInstallId, generation);
            }
        }
        catch (HttpRequestException)
        {
            if (activeSnapshot is { } current &&
                current.Game.EffectiveInstallId == game.EffectiveInstallId &&
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
                current.Game.EffectiveInstallId == game.EffectiveInstallId &&
                current.Generation == generation)
            {
                ApplySnapshot(current with
                {
                    IsCheckingUpdates = false,
                    UpdateState = UpdateCheckState.UnableToCheck,
                    Phase = SelectionPhase.Idle
                }, CurrentState);
                if (!automatic) SetSelectionError($"Update check failed: {exception.Message}", game.EffectiveInstallId, generation);
            }
        }
        finally
        {
            if (activeSnapshot is { IsCheckingUpdates: true } current &&
                current.Game.EffectiveInstallId == game.EffectiveInstallId &&
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

    private static int ConfidenceRank(string confidence) => confidence switch
    {
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0
    };

    private string CustomSourceRoot(string providerId) =>
        Preferences.CustomSourceRoots.GetValueOrDefault(providerId) ?? string.Empty;

    private void SetCustomSourceRoot(string providerId, string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (CustomSourceRoot(providerId) == normalized) return;
        if (normalized.Length == 0) Preferences.CustomSourceRoots.Remove(providerId);
        else Preferences.CustomSourceRoots[providerId] = normalized;
        OnPropertyChanged(providerId switch
        {
            "heroic" => nameof(HeroicCustomRoot),
            "legendary" => nameof(LegendaryCustomRoot),
            "lutris" => nameof(LutrisCustomRoot),
            "bottles" => nameof(BottlesCustomRoot),
            _ => nameof(MinigalaxyCustomRoot)
        });
        SchedulePreferencesSave();
        ScheduleProviderRefresh();
    }

    public Task SaveOverridesAsync(
        string? executable,
        string? deploymentDirectory,
        CancellationToken cancellationToken = default) =>
        SaveOverridesAsync(executable, deploymentDirectory,
            applicationState.Overrides.GetValueOrDefault(RequireGame().EffectiveInstallId)?.Prefix,
            cancellationToken);

    public async Task SaveOverridesAsync(
        string? executable,
        string? deploymentDirectory,
        string? prefix,
        CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var game = RequireGame();
        var validation = GameOverrideValidator.Validate(game, executable, deploymentDirectory, prefix);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        var reset = string.IsNullOrWhiteSpace(executable) && string.IsNullOrWhiteSpace(deploymentDirectory) &&
                    string.IsNullOrWhiteSpace(prefix);
        if (reset)
            applicationState.Overrides.Remove(game.EffectiveInstallId);
        else applicationState.Overrides[game.EffectiveInstallId] = validation.Override;
        await stateStore.SaveAsync(applicationState, cancellationToken);
        var automaticExecutable = game.CandidateList
            .Where(candidate => File.Exists(candidate.Path) && candidate.Confidence != DetectionConfidence.None)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path.Length)
            .Select(candidate => candidate.Path)
            .FirstOrDefault() ?? game.Sources.Select(source => source.ExecutableHint).FirstOrDefault(File.Exists);
        var automaticPrefix = game.Sources.Select(source => source.PrefixHint)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        var effectiveExecutable = reset ? automaticExecutable : validation.Override.Executable ?? game.Executable;
        var effectiveDeployment = reset
            ? effectiveExecutable is null ? game.GameRoot : Path.GetDirectoryName(effectiveExecutable) ?? game.GameRoot
            : validation.Override.DeploymentDirectory ??
            (validation.Override.Executable is null
                ? game.DeploymentDirectory
                : Path.GetDirectoryName(validation.Override.Executable)) ?? game.GameRoot;
        var effective = game with
        {
            Executable = effectiveExecutable,
            DeploymentDirectory = effectiveDeployment,
            Prefix = reset ? automaticPrefix : validation.Override.Prefix ?? game.Prefix,
            Confidence = reset ? game.CandidateList.FirstOrDefault(candidate =>
                effectiveExecutable is not null && PathsEqual(candidate.Path, effectiveExecutable))?.Confidence ?? game.Confidence :
                validation.Override.Executable is null ? game.Confidence : DetectionConfidence.High,
            SelectionReason = reset ? "Selected again using launcher metadata and shared game analysis." :
                validation.Override.Executable is null ? game.SelectionReason :
                "Selected because a persistent manual executable override is configured."
        };
        var index = games.FindIndex(item => item.InstallId.Equals(game.InstallId));
        if (index >= 0) games[index] = effective;
        readinessService.Invalidate(game.InstallId);
        ApplyFilter();
        GlobalStatus = "Game correction saved";
        await SelectAsync(effective, cancellationToken);
    }

    public Task ResetOverridesAsync(CancellationToken cancellationToken = default) =>
        SaveOverridesAsync(null, null, null, cancellationToken);

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
            var target = snapshot.Game.ToDeploymentTarget();
            var result = action == "repair"
                ? await planner.BuildRepairPlanAsync(target, component, artifact, cancellationToken: token)
                : await planner.BuildInstallPlanAsync(target, artifact, cancellationToken: token);
            EnsureCurrentSelection(snapshot, token);
            if (!SetSelectionStatus("Deployment plan ready for review", snapshot.Game.EffectiveInstallId, snapshot.Generation, token, UiState.Ready))
                throw new OperationCanceledException("The selected game changed while the plan was being prepared.", token);
            return StampPlan(result, snapshot);
        }
        catch (OperationCanceledException)
        {
            SetSelectionStatus("Plan cancelled", snapshot.Game.EffectiveInstallId, snapshot.Generation);
            throw;
        }
        catch
        {
            SetSelectionStatus("Could not prepare deployment", snapshot.Game.EffectiveInstallId, snapshot.Generation);
            throw;
        }
        finally
        {
            EndBusy(busy);
            if (CurrentState == UiState.Planning)
                SetSelectionStatus(GlobalStatus, snapshot.Game.EffectiveInstallId, snapshot.Generation, state: UiState.Ready);
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
            if (planningSnapshot.PrimaryAction == PrimaryActionKind.Repair)
            {
                var local = await new ComponentDetector().DetectStackAsync(
                    snapshot.Game.ToDeploymentTarget(), snapshot.Generation, token);
                if ((local.ConcreteDefects is null || local.ConcreteDefects.Count == 0) &&
                    local.Components.All(x => x.State is not ComponentLifecycleState.RepairRequired))
                {
                    var healthy = new DeploymentPlan
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        InstallId = snapshot.Game.EffectiveInstallId,
                        SteamAppId = snapshot.Game.SteamAppId,
                        GameRoot = snapshot.Game.GameRoot,
                        DeploymentDirectory = snapshot.Game.DeploymentDirectory ?? snapshot.Game.GameRoot,
                        SelectionGeneration = snapshot.Generation,
                        Action = "repair recommended setup",
                        CompatibilityMessage = "No repair needed. The installed stack is healthy.",
                        LaunchOption = local.LaunchOptionRequirement,
                        Warnings = ["No repair needed. The installed stack is healthy."],
                        ExpectedComponentStates = local.Components
                            .Where(x => x.State is ComponentLifecycleState.InstalledHealthy or
                                ComponentLifecycleState.InstalledWithWarnings or
                                ComponentLifecycleState.InstalledMetadataIncomplete or
                                ComponentLifecycleState.UpdateAvailable)
                            .Select(x => new ComponentStateExpectation(x.Component, true))
                            .ToList()
                    };
                    if (!SetSelectionStatus("No repair needed", snapshot.Game.EffectiveInstallId, snapshot.Generation, token, UiState.Ready))
                        throw new OperationCanceledException("The selected game changed while the plan was being prepared.", token);
                    return healthy;
                }
            }
            var plan = recommendedPlanBuilder is null
                ? await BuildRecommendedStackPlanCoreAsync(planningSnapshot, token)
                : await recommendedPlanBuilder(snapshot.Game, token);
            EnsureCurrentSelection(snapshot, token);
            var result = StampPlan(ClonePlanWithAction(plan, planningSnapshot.PrimaryAction), snapshot);
            if (!SetSelectionStatus("Recommended stack ready for review", snapshot.Game.EffectiveInstallId, snapshot.Generation, token, UiState.Ready))
                throw new OperationCanceledException("The selected game changed while the plan was being prepared.", token);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetSelectionStatus("Plan cancelled", snapshot.Game.EffectiveInstallId, snapshot.Generation);
            throw;
        }
        catch
        {
            SetSelectionStatus("Could not prepare setup", snapshot.Game.EffectiveInstallId, snapshot.Generation);
            throw;
        }
        finally
        {
            EndBusy(busy);
            if (CurrentState == UiState.Planning)
                SetSelectionStatus(GlobalStatus, snapshot.Game.EffectiveInstallId, snapshot.Generation, state: UiState.Ready);
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
        if (componentAction == PrimaryActionKind.Repair)
        {
            var local = await new ComponentDetector().DetectStackAsync(
                snapshot.Game.ToDeploymentTarget(), snapshot.Generation, cancellationToken);
            var report = local.ReportFor(component);
            var reportHealthy = report is not null &&
                report.State is (ComponentLifecycleState.InstalledHealthy or
                    ComponentLifecycleState.InstalledWithWarnings or
                    ComponentLifecycleState.InstalledMetadataIncomplete or
                    ComponentLifecycleState.UpdateAvailable);
            if (reportHealthy &&
                string.IsNullOrWhiteSpace(report!.Evidence.RepairReason) &&
                (local.ConcreteDefects is null || local.ConcreteDefects.Count == 0))
            {
                var healthy = new DeploymentPlan
                {
                    Id = Guid.NewGuid().ToString("N"),
                    InstallId = snapshot.Game.EffectiveInstallId,
                    SteamAppId = snapshot.Game.SteamAppId,
                    GameRoot = snapshot.Game.GameRoot,
                    DeploymentDirectory = snapshot.Game.DeploymentDirectory ?? snapshot.Game.GameRoot,
                    SelectionGeneration = snapshot.Generation,
                    Action = $"repair {component}",
                    CompatibilityMessage = "No repair needed. The installed stack is healthy.",
                    LaunchOption = local.LaunchOptionRequirement,
                    Warnings = ["No repair needed. The installed stack is healthy."],
                    ExpectedComponentStates = local.Components
                        .Where(x => x.State is ComponentLifecycleState.InstalledHealthy or
                            ComponentLifecycleState.InstalledWithWarnings or
                            ComponentLifecycleState.InstalledMetadataIncomplete or
                            ComponentLifecycleState.UpdateAvailable)
                        .Select(x => new ComponentStateExpectation(x.Component, true))
                        .ToList()
                };
                SetSelectionStatus("No repair needed", snapshot.Game.EffectiveInstallId, snapshot.Generation, state: UiState.Ready);
                return healthy;
            }
        }
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
            if (!SetSelectionStatus($"{card.Name} plan ready for review", snapshot.Game.EffectiveInstallId,
                    snapshot.Generation, token, UiState.Ready))
                throw new OperationCanceledException(
                    "The selected game changed while the plan was being prepared.", token);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetSelectionStatus("Plan cancelled", snapshot.Game.EffectiveInstallId, snapshot.Generation);
            throw;
        }
        catch
        {
            SetSelectionStatus("Could not prepare setup", snapshot.Game.EffectiveInstallId, snapshot.Generation);
            throw;
        }
        finally
        {
            EndBusy(busy);
            if (CurrentState == UiState.Planning)
                SetSelectionStatus(GlobalStatus, snapshot.Game.EffectiveInstallId, snapshot.Generation, state: UiState.Ready);
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
            var result = StampPlan(await planner.BuildRemoveStackPlanAsync(snapshot.Game.ToDeploymentTarget(), linkedCancellation.Token), snapshot);
            EnsureCurrentSelection(snapshot, linkedCancellation.Token);
            if (!SetSelectionStatus("Removal plan ready for review", snapshot.Game.EffectiveInstallId, snapshot.Generation,
                    linkedCancellation.Token, UiState.Ready))
                throw new OperationCanceledException("The selected game changed while the plan was being prepared.", linkedCancellation.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetSelectionStatus("Plan cancelled", snapshot.Game.EffectiveInstallId, snapshot.Generation);
            throw;
        }
        catch
        {
            SetSelectionStatus("Could not prepare removal", snapshot.Game.EffectiveInstallId, snapshot.Generation);
            throw;
        }
        finally
        {
            EndBusy(busy);
            if (CurrentState == UiState.Planning)
                SetSelectionStatus(GlobalStatus, snapshot.Game.EffectiveInstallId, snapshot.Generation, state: UiState.Ready);
        }
    }

    public async Task<DeploymentPlan> BuildRemovePlanAsync(ComponentKind component, CancellationToken cancellationToken = default)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linkedCancellation = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var result = StampPlan(
            await planner.BuildRemovePlanAsync(snapshot.Game.ToDeploymentTarget(), component, cancellationToken: linkedCancellation.Token),
            snapshot);
        EnsureCurrentSelection(snapshot, linkedCancellation.Token);
        return result;
    }

    public async Task<DeploymentPlan> BuildRestorePlanAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        using var linkedCancellation = CreateSelectionLinkedCancellation(snapshot, cancellationToken);
        var result = StampPlan(await planner.BuildRestorePlanAsync(snapshot.Game.ToDeploymentTarget(), linkedCancellation.Token), snapshot);
        EnsureCurrentSelection(snapshot, linkedCancellation.Token);
        return result;
    }

    public DeploymentPlan BuildInterruptedRecoveryPlan()
    {
        var snapshot = activeSnapshot ?? throw new InvalidOperationException("Select a game first.");
        if (!DeploymentRecoveryProbe.Probe(snapshot.Game.GameRoot).HasInterruptedTransaction)
            throw new InvalidOperationException("No interrupted operation requires recovery.");
        return StampPlan(new DeploymentPlan
        {
            Id = Guid.NewGuid().ToString("N"),
            InstallId = snapshot.Game.EffectiveInstallId,
            SteamAppId = snapshot.Game.SteamAppId,
            GameRoot = snapshot.Game.GameRoot,
            DeploymentDirectory = snapshot.Game.DeploymentDirectory ?? snapshot.Game.GameRoot,
            Action = "Recover interrupted operation",
            RequiresConfirmation = true,
            CompatibilityMessage = "Restore the transaction's recorded pre-operation file state. Foreign files are never overwritten silently."
        }, snapshot);
    }

    private async Task<DeploymentPlan> BuildRecommendedStackPlanCoreAsync(
        GameSelectionSnapshot snapshot,
        CancellationToken cancellationToken,
        ComponentKind? requestedComponent = null)
    {
        void ReportStage(string stage) =>
            SetSelectionStatus(stage, snapshot.Game.EffectiveInstallId, snapshot.Generation, cancellationToken, UiState.Planning);

        ReportStage("Checking compatibility");
        var preflight = await Task.Run(() => ManagedArchiveExtractor.ProbeCapabilities(
            paths.AppCacheDirectory, Path.Combine(paths.AppCacheDirectory, "tmp")), cancellationToken);
        if (!preflight.RequiredCapabilitiesAvailable)
            throw new ArtifactPipelineException(requestedComponent ?? ComponentKind.ReShade,
                ArtifactPipelineStage.Resolve,
                "Managed archive inspection is unavailable on this system. No game files were changed.",
                technicalDetail: preflight.Summary);

        var catalog = new GameProfileCatalog(paths);
        using var httpClient = new HttpClient();
        var resolver = new OfficialArtifactResolver(httpClient, paths, catalog);
        ReportStage("Resolving official files");
        var resolution = await resolver.ResolveAsync(snapshot.Game.ToDeploymentTarget(), true, cancellationToken, forceRefresh: true);
        var game = ApplyOfficialDeploymentHint(snapshot.Game, resolution);
        var requestReno = (requestedComponent is null or ComponentKind.RenoDx) &&
            (resolution.CanAcquireRenoSetup || resolution.CanAcquireRenoDx) &&
            ShouldTargetComponent(snapshot, ComponentKind.RenoDx) &&
            HasSafeRenoDependency(snapshot);
        var requestIndependentReShade = (requestedComponent is null or ComponentKind.ReShade) &&
            (requestedComponent == ComponentKind.ReShade ||
             snapshot.PrimaryAction is PrimaryActionKind.Install or PrimaryActionKind.Update or PrimaryActionKind.Repair) &&
            ShouldTargetComponent(snapshot, ComponentKind.ReShade) &&
            resolution.Artifacts.Any(x => x.Component == ComponentKind.ReShade) &&
            (!requestReno || requestedComponent == ComponentKind.ReShade);
        var requestOpti = (requestedComponent is null or ComponentKind.OptiScaler) &&
            resolution.CanAcquireOptiScaler && ShouldTargetComponent(snapshot, ComponentKind.OptiScaler);
        if (!requestIndependentReShade && !requestReno && !requestOpti)
            throw new ArtifactPipelineException(requestedComponent ?? ComponentKind.ReShade,
                ArtifactPipelineStage.BuildPlan,
                FormatPlanFailure(requestedComponent, resolution,
                    "No component in the selected game state has a safe automatic deployment target."));

        var targetComponents = new HashSet<ComponentKind>();
        if (requestIndependentReShade || requestReno) targetComponents.Add(ComponentKind.ReShade);
        if (requestReno) targetComponents.Add(ComponentKind.RenoDx);
        if (requestOpti) targetComponents.Add(ComponentKind.OptiScaler);
        if (targetComponents.Contains(ComponentKind.ReShade)) ReportStage("Downloading ReShade");
        if (targetComponents.Contains(ComponentKind.RenoDx)) ReportStage("Downloading RenoDX");
        if (targetComponents.Contains(ComponentKind.OptiScaler)) ReportStage("Downloading OptiScaler");
        var started = Stopwatch.GetTimestamp();
        var progress = new Progress<ArtifactAcquisitionProgress>(update =>
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var percent = update.TotalBytes is > 0
                ? $" {update.BytesTransferred * 100.0 / update.TotalBytes.Value:0}%"
                : string.Empty;
            var bytes = update.TotalBytes is > 0
                ? $" {FormatBytes(update.BytesTransferred)}/{FormatBytes(update.TotalBytes.Value)}"
                : update.BytesTransferred > 0 ? $" {FormatBytes(update.BytesTransferred)}" : string.Empty;
            var retry = update.Attempt > 1 ? $" (retry {update.Attempt})" : string.Empty;
            ReportStage($"{update.Stage} {update.Component}{bytes}{percent}{retry} · {elapsed.TotalSeconds:0}s");
        });
        resolution = await resolver.AcquireSelectedAsync(
            resolution, targetComponents, true, cancellationToken, progress);
        ReportStage("Validating files");
        var includeReno = requestReno && (resolution.CanAcquireRenoSetup || resolution.CanAcquireRenoDx);
        var includeOpti = requestOpti && resolution.CanAcquireOptiScaler;
        var includeReShade = includeReno || requestIndependentReShade && resolution.Artifacts.Any(x =>
            x.Component == ComponentKind.ReShade && x.CacheState == ArtifactCacheState.Cached);
        if (!includeReShade && !includeReno && !includeOpti)
            throw new ArtifactPipelineException(requestedComponent ?? ComponentKind.ReShade,
                ArtifactPipelineStage.ValidateExtracted,
                FormatPlanFailure(requestedComponent, resolution, null),
                technicalDetail: string.Join('\n', resolution.Warnings));

        ReportStage("Preparing installation plan");
        var proxy = await new ProxyDiagnosticsService(catalog).DiagnoseAsync(game.ToDeploymentTarget(), cancellationToken);
        ComponentArtifact Required(ComponentKind component)
        {
            var selection = resolution.Artifacts.SingleOrDefault(x => x.Component == component && x.CachedPath is not null)
                ?? throw new ArtifactPipelineException(component, ArtifactPipelineStage.SelectPayload,
                    $"The required {component} file is unavailable. No game files were changed.");
            return CachedArtifactMaterializer.MaterializeSingle(selection);
        }
        var opti = resolution.Artifacts.SingleOrDefault(x => x.Component == ComponentKind.OptiScaler);
        (ComponentArtifact Main, IReadOnlyList<ComponentArtifact> Support, OptiScalerBundleManifest Manifest)? materializedOpti = null;
        if (includeOpti && opti is not null)
            materializedOpti = await CachedArtifactMaterializer.MaterializeOptiScalerAsync(
                artifactCache, opti, cancellationToken);
        return await planner.BuildRecommendedStackPlanAsync(game.ToDeploymentTarget(),
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
        var operationInstallId = operationContext.InstallId.Value;
        var operationGeneration = operationContext.SelectionGeneration;
        var busy = BeginBusy();
        var operationStatus = dryRun ? "Validating operation…" : "Protecting existing files";
        if (!ApplySnapshot(operationSnapshot with { Progress = operationStatus, PreviousOperationResult = null }, UiState.Deploying))
        {
            EndBusy(busy);
            return new ExecutionResult(false, dryRun, false, [],
                "The selected game changed before the operation began. No files were changed.");
        }
        SetSelectionStatus(operationStatus, operationInstallId, operationGeneration);
        if (!dryRun)
            SetSelectionStatus(OperationProgressVerb(plan), operationInstallId, operationGeneration);
        var attempted = false;
        var cancelled = false;
        var rescannedCurrentGame = false;
        ExecutionResult result;
        try
        {
            attempted = true;
            result = dryRun
                ? (await operationCoordinator.PreviewAsync(plan, cancellationToken)).Validation
                : plan.Action.StartsWith("Recover interrupted", StringComparison.OrdinalIgnoreCase)
                    ? await operationCoordinator.RecoverAsync(plan, true, cancellationToken)
                    : await operationCoordinator.ExecuteApprovedAsync(plan, true, cancellationToken);
            if (!dryRun && result.Succeeded)
                SetSelectionStatus("Verifying changes", operationInstallId, operationGeneration);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            result = new ExecutionResult(false, dryRun, false, [], "Operation cancelled.");
        }
        catch (ArtifactPipelineException exception)
        {
            result = new ExecutionResult(false, dryRun, exception.GameFilesChanged, [], exception.UserSummary);
        }
        catch (Exception exception)
        {
            result = new ExecutionResult(false, dryRun, false, [], exception.Message);
        }

        try
        {
            if (attempted && !dryRun && IsCurrentSelection(operationInstallId, operationGeneration) && SelectedGame is { } selected)
            {
                await SelectAsync(selected);
                rescannedCurrentGame = activeSnapshot is { } afterScan &&
                    PlanMatchesGameIdentity(plan, afterScan);
                if (result.Succeeded)
                {
                    var verification = await VerifyPostOperationAsync(plan, activeSnapshot);
                    if (verification.Outcome == PostOperationVerificationOutcome.Failed)
                    {
                        result = result with
                        {
                            Succeeded = false,
                            Error = verification.Message ??
                                "Post-operation detection did not confirm every requested component state.",
                            State = result.RolledBack
                                ? OperationLifecycleState.FailedAndRolledBack
                                : OperationLifecycleState.FailedRollbackIncomplete
                        };
                    }
                    else if (verification.Outcome == PostOperationVerificationOutcome.SucceededWithWarning)
                    {
                        result = result with
                        {
                            Succeeded = true,
                            State = OperationLifecycleState.SucceededWithWarning,
                            Warning = verification.Message ??
                                "Changes completed. Some status details could not be refreshed yet.",
                            Error = null
                        };
                    }
                    else
                    {
                        result = result with
                        {
                            Succeeded = true,
                            State = OperationLifecycleState.Succeeded,
                            Error = null
                        };
                    }
                }
            }
            else if (cancelled)
            {
                result = result with
                {
                    State = result.RolledBack
                        ? OperationLifecycleState.CancelledAndRolledBack
                        : OperationLifecycleState.CancelledBeforeChanges
                };
            }
            else if (!result.Succeeded)
            {
                result = result with
                {
                    State = result.RolledBack
                        ? OperationLifecycleState.FailedAndRolledBack
                        : OperationLifecycleState.FailedBeforeChanges
                };
            }

            if (activeSnapshot is { } completedSnapshot &&
                PlanMatchesGameIdentity(plan, completedSnapshot) &&
                (rescannedCurrentGame || completedSnapshot.Generation == operationGeneration))
            {
                var succeeded = result.IsSuccessfulOutcome;
                var completedState = cancelled ? UiState.Ready : succeeded ? UiState.Success : UiState.Error;
                ApplySnapshot(completedSnapshot with { Progress = string.Empty, PreviousOperationResult = result }, completedState);
                SetSelectionError(cancelled || succeeded ? null : result.Error, completedSnapshot.Game.EffectiveInstallId, completedSnapshot.Generation);
                SetSelectionStatus(cancelled ? "Operation cancelled" : result.DryRun ? "Validation complete" :
                    succeeded
                        ? (result.State == OperationLifecycleState.SucceededWithWarning
                            ? (result.Warning ?? "Changes completed. Status refresh is still finishing.")
                            : plan.Action.Contains("restore", StringComparison.OrdinalIgnoreCase)
                                ? "The game files were restored."
                                : "Changes completed")
                        : "Operation failed",
                    completedSnapshot.Game.EffectiveInstallId, completedSnapshot.Generation);
                if (!dryRun)
                {
                    readinessService.Invalidate(completedSnapshot.Game.InstallId);
                    StartBackground(() => RefreshReadinessAsync(true, cancellationToken));
                }
            }
            if (!dryRun)
            {
                await applicationLog.WriteAsync(new(DateTimeOffset.UtcNow,
                    result.IsSuccessfulOutcome ? ApplicationLogLevel.Information : ApplicationLogLevel.Error,
                    "game-operation",
                    $"{plan.Action}: {(result.IsSuccessfulOutcome ? "completed" : "failed")}",
                    result.State.ToString()), CancellationToken.None);
                try
                {
                    var summary = DeploymentPlanSummary.From(plan);
                    await activityHistoryStore.AppendAsync(new(
                        Guid.NewGuid().ToString("N"),
                        DateTimeOffset.UtcNow,
                        new GameInstallId(plan.InstallId),
                        activeSnapshot?.Game.Name ?? plan.InstallId,
                        plan.Action,
                        result.IsSuccessfulOutcome
                            ? summary.ManualLauncherStepRequired ? ActivityOutcome.SuccessWithManualStep : ActivityOutcome.Success
                            : result.RolledBack ? ActivityOutcome.RolledBack : ActivityOutcome.Failed,
                        summary.FilesAdded + summary.FilesReplaced + summary.FilesRenamed,
                        summary.BackupsCreated,
                        plan.LaunchOption,
                        plan.Operations.Select(operation => operation.Target).Distinct(StringComparer.Ordinal).ToArray(),
                        result.Error ?? result.Warning), CancellationToken.None);
                    await ReloadActivityHistoryAsync();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   System.Text.Json.JsonException)
                {
                    GlobalStatus = $"{GlobalStatus} · activity history could not be saved";
                }
            }
            return result;
        }
        finally
        {
            EndBusy(busy);
            if (activeSnapshot is { } current &&
                current.Game.EffectiveInstallId == operationInstallId &&
                (current.Generation == operationGeneration || rescannedCurrentGame))
                ApplySnapshot(current with { Progress = string.Empty }, CurrentState);
        }
    }

    public bool IsPlanForCurrentSelection(DeploymentPlan plan) =>
        activeSnapshot is { } snapshot && PlanMatchesSnapshot(plan, snapshot);

    public void CancelBackgroundWork()
    {
        libraryReadinessCancellation?.Cancel();
        Interlocked.Increment(ref libraryReadinessGeneration);
        IsEvaluatingLibraryReadiness = false;
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

        RaiseSelectionProperties();
    }

    public void CollapseComponentDetails() => CollapseAllDetails();

    public async Task SavePreferencesAsync(CancellationToken cancellationToken = default)
    {
        await preferencesSaveLock.WaitAsync(cancellationToken);
        try
        {
            await preferencesStore.SaveAsync(Preferences, cancellationToken);
        }
        finally
        {
            preferencesSaveLock.Release();
        }
    }

    private void SchedulePreferencesSave()
    {
        if (disposed) return;
        var task = SavePreferencesSafelyAsync();
        lock (backgroundTaskLock) pendingPreferencesSave = task;
    }

    private async Task SavePreferencesSafelyAsync()
    {
        try
        {
            await SavePreferencesAsync();
        }
        catch (Exception exception)
        {
            if (!disposed) ErrorMessage = $"Could not save settings: {exception.Message}";
        }
    }

    public void DismissError()
    {
        ErrorMessage = null;
        if (CurrentState == UiState.Error) CurrentState = SelectedGame is null ? UiState.Empty : UiState.Ready;
    }

    private void ApplyFilter()
    {
        var updates = componentUpdates.Select(item => new GameInstallId(item.InstallId)).ToHashSet();
        LibraryPage.Apply(games, LibraryFilter, SearchText, Preferences.ShowUnsupportedNativeGames,
            readinessSummaries, updates);
        FilteredGames = LibraryPage.Games;
        OnPropertyChanged(nameof(FilteredGameCount));
        RaiseStateProperties();
    }

    private InstalledGame? FindGame(GameInstallId installId) =>
        games.FirstOrDefault(game => game.InstallId.Equals(installId));

    private async Task ReloadActivityHistoryAsync()
    {
        RecentOperations = await activityHistoryStore.LoadAsync();
    }

    private async Task RelinkMovedProfilesAsync(
        IReadOnlyList<InstalledGame> previousGames,
        CancellationToken cancellationToken)
    {
        var updated = profiles.ToDictionary(pair => pair.Key, pair => pair.Value);
        var changed = false;
        foreach (var profile in profiles.Values.Where(item => FindGame(item.InstallId) is null))
        {
            var previous = previousGames.FirstOrDefault(game => game.InstallId.Equals(profile.InstallId));
            if (previous is null || string.IsNullOrWhiteSpace(previous.ExternalId)) continue;
            var matches = games.Where(game => game.Store == previous.Store &&
                                              game.PrimaryLauncher == previous.PrimaryLauncher &&
                                              string.Equals(game.ExternalId, previous.ExternalId,
                                                  StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) continue;
            var relinked = UserGameProfileRelinker.TryRelink(
                profile, previous, matches[0], DetectionConfidence.High);
            if (relinked is null) continue;
            updated.Remove(profile.InstallId);
            updated[relinked.InstallId] = relinked;
            changed = true;
        }
        if (!changed) return;
        await profileStore.SaveAsync(updated.Values, cancellationToken);
        profiles = updated;
        OnPropertyChanged(nameof(Profiles));
        OnPropertyChanged(nameof(ProfilesNeedingReviewCount));
    }

    private void RaiseOverviewProperties()
    {
        OverviewPage.Update(games, readinessSummaries, OverviewUpdateCount, DiagnosticWarningCount);
        SettingsPage.Update(Preferences);
        OnPropertyChanged(nameof(OverviewGameCount));
        OnPropertyChanged(nameof(OverviewWindowsCount));
        OnPropertyChanged(nameof(OverviewAntiCheatCount));
        OnPropertyChanged(nameof(OverviewUnsupportedCount));
        OnPropertyChanged(nameof(OverviewInstallingCount));
        OnPropertyChanged(nameof(OverviewEnabledSourceCount));
        OnPropertyChanged(nameof(OverviewReadyCount));
        OnPropertyChanged(nameof(OverviewNeedsAttentionCount));
        OnPropertyChanged(nameof(OverviewUnsupportedReadinessCount));
        OnPropertyChanged(nameof(OverviewReadinessPendingCount));
        OnPropertyChanged(nameof(OverviewUpdateCount));
        OnPropertyChanged(nameof(OverviewRecoveryCount));
        OnPropertyChanged(nameof(OverviewSummary));
        OnPropertyChanged(nameof(OverviewAttentionSummary));
        OnPropertyChanged(nameof(OverviewSourceSummary));
        OnPropertyChanged(nameof(OverviewDiagnosticSummary));
        OnPropertyChanged(nameof(OverviewUpdateSummary));
    }

    private IReadOnlyList<ComponentCardViewModel> CreateComponentCards(
        IReadOnlyList<ComponentStatus> statuses,
        IReadOnlyList<ResolvedArtifact>? resolved,
        GameSelectionSnapshot? expansionSource,
        Uri? officialPageUrl = null,
        IReadOnlyList<string>? compatibilityNotes = null)
    {
        var expandedCard = expansionSource?.ComponentCards.SingleOrDefault(x => x.IsDetailsExpanded);
        var cards = statuses.Select(status =>
        {
            var item = resolved?.SingleOrDefault(x => x.Component == status.Component);
            return new ComponentCardViewModel(status, item, ExpandDetails,
                status.Component == ComponentKind.RenoDx ? officialPageUrl ?? item?.OfficialSource : null,
                status.Component == ComponentKind.RenoDx ? compatibilityNotes : null);
        }).ToArray();
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
        if (report is null)
            return BuildLocalSupportSummary(profile, eligibility);
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
        if (report.ArtifactResolution.CanAcquireRenoDx ||
            report.Profile.Profile.RenoDxSupport is GameProfileSupport.Supported or GameProfileSupport.EngineFallback)
        {
            var renoTitle = report.ArtifactResolution.RenoDxCompatibility switch
            {
                RenoDxCompatibilityState.InProgress => "Exact RenoDX addon found (in progress)",
                RenoDxCompatibilityState.ExactAddonAvailableFromOfficialSnapshotRelease => "Exact RenoDX addon available",
                RenoDxCompatibilityState.ExactAddonAvailableFromOfficialDiscussion => "Exact RenoDX addon available",
                RenoDxCompatibilityState.GenericUnityAddonAvailable => "Generic Unity RenoDX addon available",
                RenoDxCompatibilityState.GenericUnrealAddonAvailable => "Generic Unreal RenoDX addon available",
                RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable =>
                    "Exact RenoDX addon available for another executable",
                _ => "Exact RenoDX addon found"
            };
            return new(renoTitle,
                report.ArtifactResolution.Artifacts.FirstOrDefault(x => x.Component == ComponentKind.RenoDx)
                    ?.DeployFileName is { } fileName
                    ? $"Working addon: {fileName}."
                    : LocalReadyMessage(report.Profile, eligibility),
                report.CanInstallRecommendedStack || report.ArtifactResolution.CanAcquireRenoDx);
        }
        var mapped = MapRenoCompatibilityTitle(report.ArtifactResolution.RenoDxCompatibility);
        if (mapped is not null)
            return new(mapped.Value.Title, mapped.Value.Message,
                eligibility.CanInstall || report.CanInstallRecommendedStack);
        if (profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported && !eligibility.CanInstall)
            return new(GameSummary(eligibility.Explanation),
                HumanizeSupportDetail(eligibility.Explanation) ?? eligibility.Explanation, false);
        if (!report.ArtifactResolution.IsFullyAutomatic && !report.ArtifactResolution.CanAcquireRenoDx)
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
                    "No RenoDX addon found. ReShade and OptiScaler are still available.",
                true);
        if (profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported)
            return new("Not listed",
                "No RenoDX addon found. ReShade and OptiScaler are still available when supported.",
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

    private static (string Title, string Message)? MapRenoCompatibilityTitle(RenoDxCompatibilityState state) => state switch
    {
        RenoDxCompatibilityState.ListedManualDownloadRequired =>
            ("Listed by RenoDX, manual download required",
                "This game is listed by RenoDX, but no direct addon download is published."),
        RenoDxCompatibilityState.OfficialPageAvailableNoDirectAddon =>
            ("Official RenoDX page found", "No direct addon download is available."),
        RenoDxCompatibilityState.MultipleOfficialFilesRequireConfirmation =>
            ("Confirmation required", "Multiple official Discussion addon files require confirmation."),
        RenoDxCompatibilityState.AddonArchitectureMismatch =>
            ("Architecture mismatch", "Addon found, but no compatible executable architecture is available."),
        RenoDxCompatibilityState.OfficialSourceTemporarilyUnavailable =>
            ("Official source unavailable", "The official RenoDX Discussion could not be refreshed right now."),
        RenoDxCompatibilityState.UnsafeArtifactRejected =>
            ("Unsafe artifact rejected", "A Discussion artifact failed safety validation."),
        RenoDxCompatibilityState.AmbiguousMatch =>
            ("Ambiguous RenoDX match", "Multiple RenoDX catalog candidates require confirmation."),
        RenoDxCompatibilityState.MetadataUnavailable =>
            ("RenoDX catalog unavailable", "ReShade and OptiScaler remain usable when supported."),
        RenoDxCompatibilityState.NotListed or RenoDxCompatibilityState.NoAddonFound =>
            ("Not listed", "No RenoDX addon found. ReShade and OptiScaler are still available when supported."),
        RenoDxCompatibilityState.AddonAvailableExecutableSelectionRequired =>
            ("Executable selection required", "Addon found, but no compatible executable was selected."),
        RenoDxCompatibilityState.AddonAvailableArchitectureUnconfirmed =>
            ("Architecture unconfirmed", "Architecture could not be confirmed for the RenoDX addon."),
        RenoDxCompatibilityState.AntiCheatOrSafetyRestriction =>
            ("Safety restriction", "Anti-cheat or another safety restriction blocks RenoDX."),
        RenoDxCompatibilityState.UnsupportedEngineOrApi =>
            ("Unsupported engine/API", "This engine or API is not supported by RenoDX."),
        RenoDxCompatibilityState.Unsupported =>
            ("Unsupported", "RenoDX cannot safely apply to this game."),
        RenoDxCompatibilityState.SupersededByGenericAddon =>
            ("Superseded by generic addon", "The old exact profile was superseded by a generic RenoDX addon."),
        RenoDxCompatibilityState.OfflineCatalogInUse =>
            ("Offline catalog in use", "Using the last known-good RenoDX catalog."),
        _ => null
    };

    public void OpenOfficialPage(ComponentCardViewModel card)
    {
        if (card.OfficialPageUrl is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = card.OfficialPageUrl.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            GlobalStatus = $"Could not open the official page: {exception.Message}";
        }
    }

    private static InstalledGame ApplyOfficialDeploymentHint(InstalledGame game, GameArtifactResolution resolution)
    {
        var relative = resolution.RecommendedDeploymentRelativeDirectory;
        if (string.IsNullOrWhiteSpace(relative)) return game;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.GameRoot));
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.Equals(root, StringComparison.Ordinal) &&
            !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return game;
        return game with { DeploymentDirectory = candidate };
    }

    private static bool CanPrepareAutomaticPlan(StackStatusReport report, PrimaryActionKind action)
    {
        if (action is not (PrimaryActionKind.Install or PrimaryActionKind.Update or PrimaryActionKind.Repair)) return false;
        var components = report.Components;
        var reno = (report.ArtifactResolution.CanAcquireRenoSetup || report.ArtifactResolution.CanAcquireRenoDx) &&
            ShouldTargetComponent(components, ComponentKind.RenoDx, action) && HasSafeRenoDependency(components);
        var reshade = ShouldTargetComponent(components, ComponentKind.ReShade, action) &&
            report.ArtifactResolution.Artifacts.Any(x => x.Component == ComponentKind.ReShade);
        var opti = report.OptiScalerEligibility.CanInstall &&
            report.ArtifactResolution.CanAcquireOptiScaler &&
            ShouldTargetComponent(components, ComponentKind.OptiScaler, action);
        return reno || reshade || opti;
    }

    public void OpenCacheFolder()
    {
        Directory.CreateDirectory(paths.AppCacheDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = paths.AppCacheDirectory,
            UseShellExecute = true
        });
    }

    private static string FormatPlanFailure(
        ComponentKind? requestedComponent,
        GameArtifactResolution resolution,
        string? fallback)
    {
        var relevant = resolution.Warnings.Where(warning =>
        {
            if (warning.Contains("No supported game profile", StringComparison.OrdinalIgnoreCase) &&
                requestedComponent is not ComponentKind.RenoDx)
                return false;
            if (requestedComponent is { } component)
            {
                if (warning.Contains("acquisition failed", StringComparison.OrdinalIgnoreCase) ||
                    warning.Contains("could not be prepared", StringComparison.OrdinalIgnoreCase) ||
                    warning.Contains("was requested", StringComparison.OrdinalIgnoreCase))
                    return warning.Contains(component.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }).ToArray();
        if (relevant.Length > 0) return string.Join('\n', relevant);
        return fallback ?? (requestedComponent is { } failed
            ? $"{failed} could not be prepared. No game files were changed."
            : "No installable components are ready. No game files were changed.");
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
            PrimaryActionKind.Repair => health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled ||
                (health is ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable &&
                 components.SingleOrDefault(x => x.Component == component)?.Verification ==
                     InstallationVerification.RepairNeeded),
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
        GameDetailsPage.Update(snapshot, Readiness.Result);
        return true;
    }

    private bool HasActiveOperationStatus(string installId, long generation, CancellationToken cancellationToken)
    {
        lock (selectionLock)
        {
            return !cancellationToken.IsCancellationRequested &&
                activeSnapshot?.Game.EffectiveInstallId == installId &&
                activeSnapshot.Generation == generation &&
                generation == Volatile.Read(ref selectionGeneration) &&
                (activeSnapshot.PreviousOperationResult is not null ||
                 activeSnapshot.Progress.Length > 0 || currentState == UiState.Deploying);
        }
    }

    private void UpdateLinuxDiagnostics(InstalledGame game, string? requiredLaunchOption)
    {
        var inspection = ProtonInspector.Inspect(game);
        ProtonSummary = inspection.Summary;
        var hdr = HdrReadinessDoctor.Evaluate(platformCapabilities);
        HdrSummary = $"{hdr.Status}: {hdr.Summary}";
        LaunchCompositionText = string.IsNullOrWhiteSpace(requiredLaunchOption)
            ? "%command%"
            : requiredLaunchOption;
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
        Readiness.SetResult(null);
        GameDetailsPage.Clear();
        RaiseSelectionProperties();
    }

    private async Task EvaluateReadinessForSelectionAsync(
        InstalledGame game,
        long generation,
        IReadOnlyList<ComponentStatus>? components,
        ProxySelectionResult? proxy,
        string? launchOption,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (!IsCurrentSelection(game.EffectiveInstallId, generation, cancellationToken)) return;
        Readiness.IsLoading = true;
        try
        {
            applicationState.Overrides.TryGetValue(game.EffectiveInstallId, out var manualOverride);
            profiles.TryGetValue(game.InstallId, out var profile);
            var result = await readinessService.EvaluateAsync(
                game,
                new GameReadinessEvaluationOptions(
                    AllowNetwork: false,
                    ForceRefresh: force,
                    BuildRecommendedPlan: true,
                    WarnBeforeAntiCheatDeployments: Preferences.WarnBeforeAntiCheatDeployments,
                    PreferExistingManagedVersions: Preferences.PreferExistingManagedVersions,
                    PrefetchedComponents: components,
                    PrefetchedProxyName: proxy?.SelectedProxy,
                    PrefetchedLaunchOption: launchOption,
                    ManualOverride: manualOverride,
                    Profile: profile),
                cancellationToken);
            if (!IsCurrentSelection(game.EffectiveInstallId, generation, cancellationToken)) return;
            Readiness.SetResult(result);
            GameDetailsPage.Update(activeSnapshot!, result);
            RecordReadinessSummary(game, result);
            UpdateComponentUpdatesFromSelection(game, result);
            OnPropertyChanged(nameof(Readiness));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsCurrentSelection(game.EffectiveInstallId, generation, cancellationToken)) return;
            var failed = new GameReadinessResult(
                game.InstallId,
                GameReadinessState.Error,
                "Readiness evaluation failed.",
                "Open Diagnostics",
                game.ToDeploymentTarget(),
                components ?? [],
                [new GameReadinessIssue(
                    ReadinessIssueCodes.EvaluationFailed,
                    ReadinessIssueSeverity.Blocking,
                    "Evaluation failed",
                    exception.Message,
                    "open-diagnostics")],
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                Guid.NewGuid().ToString("N"));
            Readiness.SetResult(failed);
            GameDetailsPage.Update(activeSnapshot!, failed);
            RecordReadinessSummary(game, failed);
            OnPropertyChanged(nameof(Readiness));
        }
    }

    private void StartLibraryReadinessEvaluation(CancellationToken cancellationToken)
    {
        libraryReadinessCancellation?.Cancel();
        libraryReadinessCancellation?.Dispose();
        libraryReadinessCancellation = null;

        var installedIds = games.Select(game => game.EffectiveInstallId).ToHashSet(StringComparer.Ordinal);
        foreach (var installId in readinessSummaries.Keys.Where(key => !installedIds.Contains(key)))
            readinessSummaries.TryRemove(installId, out _);

        if (!Preferences.AutomaticallyEvaluateReadiness || games.Count == 0)
        {
            IsEvaluatingLibraryReadiness = false;
            RaiseOverviewProperties();
            ApplyFilter();
            return;
        }

        var generation = Interlocked.Increment(ref libraryReadinessGeneration);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        libraryReadinessCancellation = linked;
        var selectedInstallId = SelectedGame?.EffectiveInstallId;
        var pendingGames = games
            .Where(game => !string.Equals(game.EffectiveInstallId, selectedInstallId, StringComparison.Ordinal))
            .ToArray();
        IsEvaluatingLibraryReadiness = pendingGames.Length > 0;
        libraryReadinessTask = EvaluateLibraryReadinessAsync(pendingGames, generation, linked.Token);
    }

    private async Task EvaluateLibraryReadinessAsync(
        IReadOnlyList<InstalledGame> libraryGames,
        long generation,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, (InstalledGame Game, GameReadinessResult Result)>(StringComparer.Ordinal);
        using var concurrency = new SemaphoreSlim(2, 2);
        try
        {
            var evaluations = libraryGames.Select(async game =>
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    applicationState.Overrides.TryGetValue(game.EffectiveInstallId, out var manualOverride);
                    profiles.TryGetValue(game.InstallId, out var profile);
                    var result = await readinessService.EvaluateAsync(
                        game,
                        new GameReadinessEvaluationOptions(
                            AllowNetwork: false,
                            BuildRecommendedPlan: false,
                            WarnBeforeAntiCheatDeployments: Preferences.WarnBeforeAntiCheatDeployments,
                            PreferExistingManagedVersions: Preferences.PreferExistingManagedVersions,
                            ManualOverride: manualOverride,
                            Profile: profile),
                        cancellationToken);
                    results[game.EffectiveInstallId] = (game, result);
                }
                finally
                {
                    concurrency.Release();
                }
            });
            await Task.WhenAll(evaluations);
            if (cancellationToken.IsCancellationRequested ||
                generation != Volatile.Read(ref libraryReadinessGeneration)) return;

            foreach (var (_, value) in results)
            {
                RecordReadinessSummary(value.Game, value.Result, refreshPresentation: false);
                UpdateComponentUpdatesFromSelection(value.Game, value.Result);
            }
            ApplyFilter();
            RaiseOverviewProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref libraryReadinessGeneration) && !disposed)
                ErrorMessage = $"Library readiness evaluation failed: {exception.Message}";
        }
        finally
        {
            if (generation == Volatile.Read(ref libraryReadinessGeneration))
                IsEvaluatingLibraryReadiness = false;
        }
    }

    private void RecordReadinessSummary(
        InstalledGame game,
        GameReadinessResult result,
        bool refreshPresentation = true)
    {
        readinessSummaries[game.EffectiveInstallId] = result.State;
        if (refreshPresentation)
        {
            ApplyFilter();
            RaiseOverviewProperties();
        }
    }

    private void UpdateComponentUpdatesFromSelection(InstalledGame game, GameReadinessResult result)
    {
        var updates = result.Components
            .Where(status => status.Health == ComponentHealth.Outdated)
            .Select(status => new ComponentUpdateItemViewModel(game, status, result.State))
            .ToArray();
        updatesByInstallId[game.EffectiveInstallId] = updates;
        RefreshComponentUpdates();
    }

    private void RefreshComponentUpdates()
    {
        ComponentUpdates = updatesByInstallId.Values.SelectMany(items => items)
            .OrderBy(item => item.GameName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ComponentName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int CountReadiness(params GameReadinessState[] states) =>
        readinessSummaries.Count(pair => states.Contains(pair.Value));

    private bool IsCurrentSelection(string? installId, long generation, CancellationToken cancellationToken = default) =>
        !cancellationToken.IsCancellationRequested &&
        !string.IsNullOrWhiteSpace(installId) &&
        activeSnapshot?.Game.EffectiveInstallId == installId &&
        activeSnapshot.Generation == generation &&
        generation == Volatile.Read(ref selectionGeneration);

    private bool SetSelectionError(
        string? message,
        string? installId,
        long generation,
        CancellationToken cancellationToken = default,
        UiState? state = null)
    {
        bool errorChanged;
        bool stateChanged;
        lock (selectionLock)
        {
            if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(installId) ||
                activeSnapshot?.Game.EffectiveInstallId != installId || activeSnapshot.Generation != generation ||
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
        string installId,
        long generation,
        CancellationToken cancellationToken = default,
        UiState? state = null)
    {
        bool statusChanged;
        bool stateChanged;
        lock (selectionLock)
        {
            if (cancellationToken.IsCancellationRequested || activeSnapshot?.Game.EffectiveInstallId != installId ||
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

    private enum PostOperationVerificationOutcome { Failed, Succeeded, SucceededWithWarning }

    private sealed record PostOperationVerification(PostOperationVerificationOutcome Outcome, string? Message);

    private async Task<PostOperationVerification> VerifyPostOperationAsync(
        DeploymentPlan plan,
        GameSelectionSnapshot? initialSnapshot)
    {
        var delays = new[] { 0, 100, 250, 500 };
        PostOperationVerification? last = null;
        foreach (var delay in delays)
        {
            if (delay > 0) await Task.Delay(delay);
            var snapshot = activeSnapshot;
            if (snapshot is null || snapshot.IsLoading || !PlanMatchesGameIdentity(plan, snapshot))
            {
                last = new(PostOperationVerificationOutcome.Failed,
                    "The selected game changed before post-operation verification finished.");
                continue;
            }

            last = await EvaluatePostOperationStateAsync(plan, snapshot);
            if (last.Outcome is PostOperationVerificationOutcome.Succeeded
                or PostOperationVerificationOutcome.SucceededWithWarning)
                return last;

            if (SelectedGame is { } selected &&
                PlanMatchesGameIdentity(plan, snapshot))
            {
                await SelectAsync(selected);
            }
        }

        if (last?.Outcome == PostOperationVerificationOutcome.Succeeded) return last;
        if (HasInstalledExpectationSatisfied(plan, activeSnapshot) ||
            HasInstalledExpectationSatisfied(plan, initialSnapshot))
        {
            return new(PostOperationVerificationOutcome.SucceededWithWarning,
                "Changes completed. Some status details could not be refreshed yet.");
        }
        return last ?? new(PostOperationVerificationOutcome.Failed,
            "Post-operation detection did not confirm every requested component state.");
    }

    private static async Task<PostOperationVerification> EvaluatePostOperationStateAsync(
        DeploymentPlan plan,
        GameSelectionSnapshot snapshot)
    {
        if (plan.ExpectedComponentStates.Count == 0)
        {
            var restoreOk = plan.Action.Contains("restore", StringComparison.OrdinalIgnoreCase) &&
                snapshot.ComponentCards.All(x => x.Health is not (ComponentHealth.Broken or
                    ComponentHealth.PartiallyInstalled or ComponentHealth.IncorrectlyConfigured or
                    ComponentHealth.RepairAvailable or ComponentHealth.ManifestUnavailable));
            return restoreOk
                ? new(PostOperationVerificationOutcome.Succeeded, null)
                : new(PostOperationVerificationOutcome.Failed,
                    "Post-operation detection did not confirm every requested component state.");
        }
        if (plan.ExpectedComponentStates.GroupBy(x => x.Component)
            .Any(group => group.Select(x => x.Installed).Distinct().Count() != 1))
            return new(PostOperationVerificationOutcome.Failed,
                "Post-operation detection did not confirm every requested component state.");

        var metadataIncomplete = false;
        foreach (var expectation in plan.ExpectedComponentStates.Distinct())
        {
            var card = snapshot.ComponentCards.SingleOrDefault(x => x.Component == expectation.Component);
            if (card is null && expectation is { Component: ComponentKind.OptiPatcher, Installed: false })
            {
                try
                {
                    var manifest = await ComponentDetector.LoadManifestAsync(snapshot.Game.GameRoot);
                    if (manifest.Files.Any(x => x.Component == ComponentKind.OptiPatcher))
                        return new(PostOperationVerificationOutcome.Failed,
                            "Post-operation detection did not confirm every requested component state.");
                    continue;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException or System.Text.Json.JsonException)
                {
                    return new(PostOperationVerificationOutcome.Failed,
                        "Post-operation detection did not confirm every requested component state.");
                }
            }
            if (card is null)
                return new(PostOperationVerificationOutcome.Failed,
                    "Post-operation detection did not confirm every requested component state.");
            if (expectation.Installed)
            {
                if (IsInstalledOutcome(card))
                {
                    if (card.State.Contains("metadata", StringComparison.OrdinalIgnoreCase) ||
                        card.Lifecycle is ComponentLifecycleState.InstalledMetadataIncomplete or
                            ComponentLifecycleState.InstalledWithWarnings)
                        metadataIncomplete = true;
                    continue;
                }
                return new(PostOperationVerificationOutcome.Failed,
                    "Post-operation detection did not confirm every requested component state.");
            }
            if (card.Health is not (ComponentHealth.Unavailable or ComponentHealth.Available or
                     ComponentHealth.Supported or ComponentHealth.DownloadRequired or ComponentHealth.Cached or
                     ComponentHealth.MissingDependency or ComponentHealth.Unsupported or ComponentHealth.Conflicting or
                     ComponentHealth.ForeignInstallation or ComponentHealth.Experimental))
            {
                return new(PostOperationVerificationOutcome.Failed,
                    "Post-operation detection did not confirm every requested component state.");
            }
        }
        return metadataIncomplete
            ? new(PostOperationVerificationOutcome.SucceededWithWarning,
                "Changes completed. Some status details could not be refreshed yet.")
            : new(PostOperationVerificationOutcome.Succeeded, null);
    }

    private static bool IsInstalledOutcome(ComponentCardViewModel card) =>
        card.Lifecycle is ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings or
            ComponentLifecycleState.InstalledMetadataIncomplete or ComponentLifecycleState.UpdateAvailable or
            ComponentLifecycleState.RepairRecommended ||
        card.Health is ComponentHealth.Installed or ComponentHealth.Outdated;

    private static bool HasInstalledExpectationSatisfied(DeploymentPlan plan, GameSelectionSnapshot? snapshot)
    {
        if (snapshot is null || !PlanMatchesGameIdentity(plan, snapshot)) return false;
        var installedExpectations = plan.ExpectedComponentStates.Where(item => item.Installed).ToArray();
        if (installedExpectations.Length == 0) return false;
        return installedExpectations.All(expectation =>
        {
            var card = snapshot.ComponentCards.SingleOrDefault(x => x.Component == expectation.Component);
            return card is not null && IsInstalledOutcome(card);
        });
    }

    private static bool PlanMatchesGameIdentity(DeploymentPlan plan, GameSelectionSnapshot snapshot) =>
        plan.InstallId == snapshot.Game.EffectiveInstallId &&
        PathsEqual(plan.GameRoot, snapshot.Game.GameRoot) &&
        PathsEqual(plan.DeploymentDirectory, snapshot.Game.DeploymentDirectory ?? snapshot.Game.GameRoot);

    private CancellationTokenSource CreateSelectionLinkedCancellation(
        GameSelectionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        lock (selectionLock)
        {
            if (!IsCurrentSelection(snapshot.Game.EffectiveInstallId, snapshot.Generation))
                throw new OperationCanceledException("The selected game changed while the plan was being prepared.");
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                selectionCancellation?.Token ?? CancellationToken.None);
        }
    }

    private void EnsureCurrentSelection(GameSelectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!IsCurrentSelection(snapshot.Game.EffectiveInstallId, snapshot.Generation, cancellationToken))
            throw new OperationCanceledException("The selected game changed while the plan was being prepared.", cancellationToken);
    }

    private static bool PlanMatchesSnapshot(DeploymentPlan plan, GameSelectionSnapshot snapshot) =>
        plan.InstallId == snapshot.Game.EffectiveInstallId &&
        (plan.SelectionGeneration == 0 || plan.SelectionGeneration == snapshot.Generation) &&
        PathsEqual(plan.GameRoot, snapshot.Game.GameRoot) &&
        PathsEqual(plan.DeploymentDirectory, snapshot.Game.DeploymentDirectory ?? snapshot.Game.GameRoot);

    private static string OperationProgressVerb(DeploymentPlan plan)
    {
        var action = plan.Action;
        if (action.Contains("remove", StringComparison.OrdinalIgnoreCase)) return "Removing";
        if (action.Contains("update", StringComparison.OrdinalIgnoreCase)) return "Updating";
        if (action.Contains("repair", StringComparison.OrdinalIgnoreCase)) return "Repairing";
        if (action.Contains("restore", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("recover", StringComparison.OrdinalIgnoreCase)) return "Restoring";
        return "Installing";
    }

    private static DeploymentPlan StampPlan(DeploymentPlan plan, GameSelectionSnapshot snapshot)
    {
        plan.SelectionGeneration = snapshot.Generation;
        return plan;
    }

    private static string ResolveLaunchOption(
        InstalledGame game,
        IReadOnlyList<ComponentStatus> statuses,
        ProxySelectionResult proxy)
    {
        var requiredProxy = ResolveManagedProxyName(statuses) ??
            (statuses.Any(x =>
                (x.Component is ComponentKind.ReShade or ComponentKind.OptiScaler) &&
                (x.Health is ComponentHealth.Installed or ComponentHealth.Outdated or
                    ComponentHealth.PartiallyInstalled or ComponentHealth.Broken ||
                    x.Lifecycle == ComponentLifecycleState.InstalledUnmanaged))
                ? proxy.SelectedProxy
                : null);
        return requiredProxy is not null && requiredProxy.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? DeploymentPlanner.GenerateLaunchOption(game.ToDeploymentTarget(), requiredProxy, IsRenoDxInstalled(statuses))
            : string.Empty;
    }

    private static bool IsRenoDxInstalled(IReadOnlyList<ComponentStatus> statuses) =>
        statuses.Any(x =>
            x.Component == ComponentKind.RenoDx &&
            (x.Health is ComponentHealth.Installed or ComponentHealth.Outdated or
                ComponentHealth.PartiallyInstalled or ComponentHealth.Broken ||
             x.Lifecycle is ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings or
                ComponentLifecycleState.InstalledMetadataIncomplete or ComponentLifecycleState.InstalledUnmanaged or
                ComponentLifecycleState.UpdateAvailable));

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
        InstallId = plan.InstallId,
        SteamAppId = plan.SteamAppId,
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

    private InstalledGame RequireGame() => SelectedGame ?? throw new InvalidOperationException("Select a game first.");
    private void RaiseSelectionProperties()
    {
        foreach (var property in new[] { nameof(SelectedGame), nameof(ComponentCards), nameof(HasSelection), nameof(ShowGame), nameof(IsSelectionLoading), nameof(ShowGameLoading), nameof(HasWarnings), nameof(WarningText), nameof(SelectedGameTitle), nameof(SelectedGameSubtitle), nameof(ExecutableDisplay), nameof(ExecutablePath), nameof(DeploymentDisplay), nameof(DeploymentPath), nameof(ProtonPrefixDisplay), nameof(ProtonPrefixPath), nameof(ConfidenceText), nameof(ArchitectureDisplay), nameof(EngineDisplay), nameof(SelectionReason), nameof(Candidates), nameof(LaunchOption), nameof(LaunchOptionDescription), nameof(HasLaunchOption), nameof(ProfileSummary), nameof(SelectedProxy), nameof(DependencySummary), nameof(TimingDiagnostics), nameof(HasTimingDiagnostics), nameof(SelectionPhase), nameof(SupportTitle), nameof(SupportMessage), nameof(CanInstallRecommendedStack), nameof(CanRunPrimarySetup), nameof(CanCheckForUpdates), nameof(IsCheckingUpdates), nameof(UpdateState), nameof(UpdateStatusText), nameof(ShowUpdateStatusChip), nameof(HasUpdates), nameof(PrimaryAction), nameof(PrimaryActionText), nameof(SelectionProgress), nameof(PreviousOperationResult), nameof(OutdatedComponents), nameof(OverviewUpdateSummary) }) OnPropertyChanged(property);
        RaiseUpdatesPageProperties();
    }
    private void RaiseStateProperties()
    {
        foreach (var property in new[] { nameof(ShowWelcome), nameof(ShowNoGames), nameof(ShowNoMatches), nameof(ShowGame), nameof(ShowGameLoading), nameof(HasWarnings), nameof(WarningText), nameof(HasFilteredGames) }) OnPropertyChanged(property);
    }
    private void RaiseUpdatesPageProperties()
    {
        UpdatesPage.Update(ComponentUpdates, UpdateState, UpdateStatusText);
        foreach (var property in new[]
                 {
                     nameof(UpdatesIsLoading), nameof(UpdatesHasAvailable), nameof(UpdatesIsOffline),
                     nameof(UpdatesIsUnavailable), nameof(UpdatesIsEmpty), nameof(OutdatedComponents),
                     nameof(OverviewUpdateSummary)
                 })
            OnPropertyChanged(property);
    }

    private static string NormalizeNavigation(string? value) => value switch
    {
        "Overview" => "Overview",
        "Updates" => "Updates",
        "Diagnostics" => "Diagnostics",
        "Settings" => "Settings",
        _ => "Library"
    };

    private int CountEnabledSources()
    {
        var count = 2;
        if (!Preferences.ScanAllSources) return 1;
        if (Preferences.EnableHeroic) count++;
        if (Preferences.EnableLegendary) count++;
        if (Preferences.EnableLutris) count++;
        if (Preferences.EnableBottles) count++;
        if (Preferences.EnableMinigalaxy) count++;
        return count;
    }

    private bool IsProviderEnabled(string providerId) => providerId.ToLowerInvariant() switch
    {
        "steam" or "manual" => true,
        "heroic" => Preferences.ScanAllSources && Preferences.EnableHeroic,
        "legendary" => Preferences.ScanAllSources && Preferences.EnableLegendary,
        "lutris" => Preferences.ScanAllSources && Preferences.EnableLutris,
        "bottles" => Preferences.ScanAllSources && Preferences.EnableBottles,
        "minigalaxy" => Preferences.ScanAllSources && Preferences.EnableMinigalaxy,
        _ => false
    };

    private void ApplySourcePanels(MultiSourceScanResult multi)
    {
        SourceDiagnosticsText = FormatSourceDiagnostics(multi);
        OnPropertyChanged(nameof(SourceDiagnosticsText));
        DiagnosticItems = multi.Diagnostics
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DiagnosticItemViewModel(item))
            .ToArray();
        ProviderStatuses = BuildProviderStatuses(multi);
        DiagnosticsPage.Update(multi.Diagnostics, ProviderStatuses, multi.Metrics ?? LibraryMetrics.Empty);
        LastScanSummary = $"Last scan {DateTimeOffset.Now:g} · gen {multi.ScanGeneration}";
        RaiseOverviewProperties();
    }

    private void ApplySourceSnapshot(LibrarySnapshot snapshot)
    {
        SourceDiagnosticsText = string.Join(Environment.NewLine, snapshot.Diagnostics.Take(80).Select(diagnostic =>
            $"{diagnostic.Severity}: [{diagnostic.ProviderId}/{diagnostic.Code}] {diagnostic.Message}"));
        OnPropertyChanged(nameof(SourceDiagnosticsText));
        DiagnosticItems = snapshot.Diagnostics
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DiagnosticItemViewModel(item))
            .ToArray();
        ProviderStatuses = KnownProviders().Select(provider =>
        {
            var status = snapshot.Providers.FirstOrDefault(item =>
                item.ProviderId.Equals(provider.Id, StringComparison.OrdinalIgnoreCase));
            return new ProviderStatusRowViewModel(
                provider.Id,
                provider.Name,
                IsProviderEnabled(provider.Id),
                status?.Available == true,
                status?.RootCount ?? 0,
                status?.RecordCount ?? 0,
                status?.Summary ?? (IsProviderEnabled(provider.Id) ? "Not detected on this system" : "Disabled in Settings"),
                string.Empty);
        }).ToArray();
        DiagnosticsPage.Update(snapshot.Diagnostics, ProviderStatuses, snapshot.Metrics);
        LastScanSummary = $"Last scan {snapshot.UpdatedAt.ToLocalTime():g} · gen {snapshot.Generation}";
        RaiseOverviewProperties();
    }

    private IReadOnlyList<ProviderStatusRowViewModel> BuildProviderStatuses(MultiSourceScanResult multi)
    {
        var rows = new List<ProviderStatusRowViewModel>();
        foreach (var (id, name) in KnownProviders())
        {
            var results = multi.ProviderResults
                .Where(result => string.Equals(result.ProviderId, id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var enabled = IsProviderEnabled(id);
            var detected = results.Any(result => result.Root.Exists);
            var roots = results.Select(result => result.Root.CanonicalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var gamesCount = results.Sum(result => result.Games.Count);
            var status = !enabled
                ? "Disabled in Settings"
                : detected
                    ? $"{gamesCount} games · {results.Count(result => result.FromCache)} cache hits"
                    : results.Select(result => result.Root.SkipReason).FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
                      ?? "Not detected on this system";
            rows.Add(new ProviderStatusRowViewModel(
                id,
                name,
                enabled,
                detected,
                roots.Length,
                gamesCount,
                status,
                roots.FirstOrDefault() ?? string.Empty));
        }

        return rows;
    }

    private IReadOnlyList<ProviderStatusRowViewModel> BuildSteamOnlyProviderStatuses(int steamGames) =>
    [
        new("steam", "Steam", true, true, steamGames > 0 ? 1 : 0, steamGames,
            Preferences.ScanAllSources ? "Steam-only scan active until multi-source refresh" : "Steam-only scanning",
            string.Empty),
        ..KnownProviders()
            .Where(provider => provider.Id is not "steam")
            .Select(provider => new ProviderStatusRowViewModel(
                provider.Id,
                provider.Name,
                IsProviderEnabled(provider.Id),
                false,
                0,
                0,
                Preferences.ScanAllSources ? "Awaiting multi-source scan" : "Disabled while Steam-only mode is on",
                string.Empty))
    ];

    private IReadOnlyList<ProviderStatusRowViewModel> BuildConfiguredProviderStatuses() =>
        KnownProviders()
            .Select(provider => new ProviderStatusRowViewModel(
                provider.Id,
                provider.Name,
                IsProviderEnabled(provider.Id),
                false,
                0,
                0,
                IsProviderEnabled(provider.Id) ? "Not scanned yet" : "Disabled in Settings",
                string.Empty))
            .ToArray();

    private static (string Id, string Name)[] KnownProviders() =>
    [
        ("steam", "Steam"),
        ("heroic", "Heroic"),
        ("legendary", "Legendary"),
        ("lutris", "Lutris"),
        ("bottles", "Bottles"),
        ("minigalaxy", "Minigalaxy"),
        ("manual", "Manual")
    ];

    private void ScheduleProviderRefresh()
    {
        var generation = Interlocked.Increment(ref providerRefreshGeneration);
        providerRefreshTask = PersistAndRefreshProvidersAsync(generation);
    }

    private async Task PersistAndRefreshProvidersAsync(long generation)
    {
        try
        {
            await SavePreferencesAsync();
            await Task.Delay(250);
            if (generation != Volatile.Read(ref providerRefreshGeneration)) return;
            if (IsBusy) return;
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError("Could not apply source settings", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        CancelBackgroundWork();
        Interlocked.Increment(ref providerRefreshGeneration);

        Task preferencesTask;
        lock (backgroundTaskLock) preferencesTask = pendingPreferencesSave;
        try
        {
            await Task.WhenAll(
                ownedBackgroundTasks.Values.Append(preferencesTask).Append(providerRefreshTask)
                    .Append(libraryReadinessTask));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            libraryReadinessCancellation?.Dispose();
            libraryReadinessCancellation = null;
            if (ownedLibraryCoordinator is not null)
                await ownedLibraryCoordinator.DisposeAsync();
            await operationCoordinator.DisposeAsync();
            ownedNetworkClient?.Dispose();
            preferencesSaveLock.Dispose();
        }
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
    private void StartBackground(Func<Task> work)
    {
        if (disposed) return;
        var generation = Interlocked.Increment(ref backgroundTaskGeneration);
        try
        {
            var observed = ObserveBackgroundAsync(generation, work());
            ownedBackgroundTasks[generation] = observed;
            if (observed.IsCompleted) ownedBackgroundTasks.TryRemove(generation, out _);
        }
        catch (Exception exception)
        {
            if (!disposed) SetError("Background operation failed", exception);
        }
    }
    private async Task ObserveBackgroundAsync(long generation, Task work)
    {
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!disposed) SetError("Background operation failed", exception);
        }
        finally
        {
            ownedBackgroundTasks.TryRemove(generation, out _);
        }
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class EmptyUserGameProfileStore : IUserGameProfileStore
    {
        private IReadOnlyDictionary<GameInstallId, UserGameProfile> values =
            new Dictionary<GameInstallId, UserGameProfile>();

        public Task<IReadOnlyDictionary<GameInstallId, UserGameProfile>> LoadAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(values);

        public Task SaveAsync(IEnumerable<UserGameProfile> profiles, CancellationToken cancellationToken = default)
        {
            values = profiles.ToDictionary(profile => profile.InstallId);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyActivityHistoryStore : IActivityHistoryStore
    {
        private readonly List<ActivityHistoryEntry> entries = [];

        public Task<IReadOnlyList<ActivityHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivityHistoryEntry>>(entries.ToArray());

        public Task AppendAsync(ActivityHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            entries.Insert(0, entry);
            return Task.CompletedTask;
        }
    }
}
