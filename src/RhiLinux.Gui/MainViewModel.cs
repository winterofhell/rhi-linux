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
            ComponentHealth.Installed => "Installed",
            ComponentHealth.Outdated => "Update available",
            ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or ComponentHealth.IncorrectlyConfigured or
                ComponentHealth.RepairAvailable => "Repair needed",
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
            ComponentHealth.Installed => "Installed files are ready to use.",
            ComponentHealth.Outdated => "A newer compatible version is available.",
            ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or ComponentHealth.IncorrectlyConfigured or
                ComponentHealth.RepairAvailable => "Some files from an earlier installation need repair.",
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
        CanRepair = !foreign && status.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or
            ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable;
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
    ExecutionResult? PreviousOperationResult)
{
    public static GameSelectionSnapshot Loading(SteamGame game, long generation, bool canCheckUpdates) => new(
        game,
        generation,
        true,
        [],
        "Loading compatibility…",
        "Checking safe options…",
        "Loading component state…",
        "Loading game status",
        "Checking installed files, ownership records, cached downloads, and safe compatibility options.",
        false,
        canCheckUpdates ? UpdateCheckState.Checking : UpdateCheckState.UnableToCheck,
        false,
        false,
        null,
        PrimaryActionKind.None,
        string.Empty,
        null);
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
        var stackStatusService = new StackStatusService(new HttpClient(), paths);
        stackStatusProvider = new StackReportProviderAdapter(stackStatusService);
        statusProvider = new StackStatusProviderAdapter(stackStatusService);
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
    public string UpdateStatusText => UpdateState switch
    {
        UpdateCheckState.UpToDate => "Up to date",
        UpdateCheckState.UpdateAvailable => "Update available",
        UpdateCheckState.Checking => "Checking",
        UpdateCheckState.Offline => "Offline",
        _ => "Unable to check"
    };
    public bool HasUpdates => activeSnapshot?.HasUpdates == true;
    public PrimaryActionKind PrimaryAction => activeSnapshot?.PrimaryAction ?? PrimaryActionKind.None;
    public string PrimaryActionText => PrimaryAction switch
    {
        PrimaryActionKind.Install => "Install",
        PrimaryActionKind.Update => "Update",
        PrimaryActionKind.Repair => "Repair and continue",
        PrimaryActionKind.Remove => "Remove",
        _ when IsSelectionLoading || IsCheckingUpdates || UpdateState == UpdateCheckState.Checking => "Checking…",
        _ when ComponentCards.Any(x => x.Health == ComponentHealth.Installed) => "Installed",
        _ => "Unavailable"
    };
    public string SupportTitle => activeSnapshot?.SupportTitle ?? "Select a game";
    public string SupportMessage => activeSnapshot?.SupportMessage ?? "Choose an installed Steam game to check compatibility.";
    public string ProfileSummary => activeSnapshot?.ProfileSummary ?? "Not resolved";
    public string SelectedProxy => activeSnapshot?.SelectedProxy ?? "Not resolved";
    public string DependencySummary => activeSnapshot?.DependencySummary ?? "Not resolved";
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
    public string SelectionReason => SelectedGame?.SelectionReason ?? string.Empty;
    public IReadOnlyList<ExecutableCandidateDisplay> Candidates => SelectedGame?.Candidates.Select(x =>
        new ExecutableCandidateDisplay(x.Score, x.Confidence.ToString(), x.Path, string.Join(" · ", x.Reasons))).ToArray() ?? [];
    public string LaunchOption => SelectedProxy.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        ? DeploymentPlanner.GenerateLaunchOption(SelectedProxy) : string.Empty;
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
            if (Preferences.CacheLimitMiB == mib) return;
            Preferences.CacheLimitMiB = Math.Max(mib, 1);
            OnPropertyChanged();
        }
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
        SetSelectionStatus($"Checking {game.Name}…", game.AppId, generation, selectionToken);
        SetSelectionError(null, game.AppId, generation);
        try
        {
            var catalog = new GameProfileCatalog(paths);
            var statusTask = statusProvider.DetectAsync(game, selectionToken);
            var profileTask = catalog.MatchAsync(game, selectionToken);
            var proxyTask = new ProxyDiagnosticsService(catalog).DiagnoseAsync(game, selectionToken);
            await Task.WhenAll(statusTask, profileTask, proxyTask);
            if (!IsCurrentSelection(game.AppId, generation, selectionToken)) return;
            var statuses = await statusTask;
            var profile = await profileTask;
            var proxy = await proxyTask;
            var eligibility = OptiScalerEligibilityService.Evaluate(game, profile, proxy);
            var cards = CreateComponentCards(statuses, null, gameChanged ? null : previousSnapshot);
            var support = BuildSupportSummary(profile, proxy, null, eligibility, false);
            var snapshot = new GameSelectionSnapshot(
                game,
                generation,
                false,
                cards,
                $"{profile.Profile.Id} · {profile.MatchReason}",
                proxy.SelectedProxy ?? "No safe proxy",
                profile.Profile.RenoDx is null
                ? $"OptiScaler: {eligibility.Level} · RenoDX unavailable"
                : $"Full-addon ReShade → {profile.Profile.RenoDx.FileName} · OptiScaler: {eligibility.Level}",
                support.Title,
                support.Message,
                support.CanInstall,
                stackStatusProvider is null ? UpdateCheckState.UnableToCheck : UpdateCheckState.Checking,
                false,
                false,
                eligibility.Level,
                PrimaryActionKind.None,
                string.Empty,
                null);
            if (!IsCurrentSelection(game.AppId, generation, selectionToken)) return;
            var state = cards.Any(x => x.Health == ComponentHealth.Conflicting) ? UiState.Conflict :
                game.RequiresConfirmation ? UiState.Unsupported : UiState.Ready;
            ApplySnapshot(snapshot, state, selectionToken);
        }
        catch (OperationCanceledException) when (selectionToken.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            SetSelectionError($"Component scan failed: {exception.Message}", game.AppId, generation, selectionToken, UiState.Error);
        }
        if (!IsCurrentSelection(game.AppId, generation, selectionToken)) return;
        if (stackStatusProvider is not null)
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
        var updateToken = linkedCancellation.Token;
        if (!IsCurrentSelection(game.AppId, generation, updateToken)) return;
        ApplySnapshot(startingSnapshot with { IsCheckingUpdates = true, UpdateState = UpdateCheckState.Checking }, CurrentState, updateToken);
        if (!automatic) SetSelectionStatus("Checking for updates…", game.AppId, generation, updateToken);
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
            var primaryAction = proposedAction == PrimaryActionKind.Remove || CanPrepareAutomaticPlan(report, proposedAction)
                ? proposedAction
                : PrimaryActionKind.None;
            var completed = new GameSelectionSnapshot(
                game,
                generation,
                false,
                cards,
                $"{report.Profile.Profile.Id} · {report.Profile.MatchReason}" +
                    (report.ArtifactResolution.RemoteManifest?.Version is { } manifestVersion ? $" · remote manifest v{manifestVersion}" : string.Empty),
                report.Proxy.SelectedProxy ?? "No safe option",
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
                null);
            if (!IsCurrentSelection(game.AppId, generation, updateToken)) return;
            if (!ApplySnapshot(completed, cards.Any(x => x.Health == ComponentHealth.Conflicting) ? UiState.Conflict :
                    game.RequiresConfirmation ? UiState.Unsupported : UiState.Ready, updateToken,
                    preserveOperationStatus: automatic)) return;
            if (!automatic || !HasActiveOperationStatus(game.AppId, generation, updateToken))
                SetSelectionStatus(UpdateStatusText, game.AppId, generation, updateToken);
        }
        catch (OperationCanceledException) when (updateToken.IsCancellationRequested)
        {
            return;
        }
        catch (HttpRequestException)
        {
            if (IsCurrentSelection(game.AppId, generation, updateToken) && activeSnapshot is { } current)
                ApplySnapshot(current with { IsCheckingUpdates = false, UpdateState = UpdateCheckState.Offline }, CurrentState, updateToken);
        }
        catch (Exception exception)
        {
            if (IsCurrentSelection(game.AppId, generation, updateToken) && activeSnapshot is { } current)
            {
                ApplySnapshot(current with { IsCheckingUpdates = false, UpdateState = UpdateCheckState.UnableToCheck }, CurrentState, updateToken);
                if (!automatic) SetSelectionError($"Update check failed: {exception.Message}", game.AppId, generation, updateToken);
            }
        }
        finally
        {
            if (IsCurrentSelection(game.AppId, generation, updateToken) && activeSnapshot is { IsCheckingUpdates: true } current)
                ApplySnapshot(current with { IsCheckingUpdates = false }, CurrentState, updateToken);
        }
    }

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
            return result;
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
            var result = ClonePlanWithAction(plan, planningSnapshot.PrimaryAction);
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
            ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or
                ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable => PrimaryActionKind.Repair,
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
            var result = ClonePlanWithAction(plan, componentAction);
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
            var result = await planner.BuildRemoveStackPlanAsync(snapshot.Game, linkedCancellation.Token);
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
        var result = await planner.BuildRemovePlanAsync(snapshot.Game, component, cancellationToken: linkedCancellation.Token);
        EnsureCurrentSelection(snapshot, linkedCancellation.Token);
        return result;
    }
    public Task<DeploymentPlan> BuildRestorePlanAsync(CancellationToken cancellationToken = default) =>
        planner.BuildRestorePlanAsync(RequireGame(), cancellationToken);

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

        var operationAppId = operationSnapshot.Game.AppId;
        var operationGeneration = operationSnapshot.Generation;
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
                if (result.Succeeded && !await PostOperationStateMatchesAsync(plan, activeSnapshot))
                    result = result with
                    {
                        Succeeded = false,
                        Error = "Post-operation detection did not confirm every requested component state."
                    };
            }

            if (activeSnapshot is { } completedSnapshot && PlanMatchesSnapshot(plan, completedSnapshot))
            {
                var completedState = cancelled ? UiState.Ready : result.Succeeded ? UiState.Success : UiState.Error;
                ApplySnapshot(completedSnapshot with { Progress = string.Empty, PreviousOperationResult = result }, completedState);
                SetSelectionError(cancelled ? null : result.Error, completedSnapshot.Game.AppId, completedSnapshot.Generation);
                SetSelectionStatus(cancelled ? "Operation cancelled" : result.DryRun ? "Validation complete" :
                    result.Succeeded ? "Changes completed" : "Operation failed",
                    completedSnapshot.Game.AppId, completedSnapshot.Generation);
            }
            return result;
        }
        finally
        {
            EndBusy(busy);
            if (activeSnapshot is { } current && current.Game.AppId == operationAppId)
                ApplySnapshot(current with { Progress = string.Empty }, CurrentState);
        }
    }

    public bool IsPlanForCurrentSelection(DeploymentPlan plan) =>
        activeSnapshot is { } snapshot && PlanMatchesSnapshot(plan, snapshot);

    public Task SavePreferencesAsync(CancellationToken cancellationToken = default) => preferencesStore.SaveAsync(Preferences, cancellationToken);

    public void DismissError()
    {
        ErrorMessage = null;
        if (CurrentState == UiState.Error) CurrentState = SelectedGame is null ? UiState.Empty : UiState.Ready;
    }

    private void ApplyFilter()
    {
        FilteredGames = string.IsNullOrWhiteSpace(SearchText) ? games.ToArray() : games.Where(x =>
            x.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            x.AppId.ToString().Contains(SearchText, StringComparison.Ordinal) ||
            x.Engine.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToArray();
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
            return new("Installation blocked", "An existing unknown game file occupies every safe compatibility filename. Remove or identify that file, then refresh.", false);
        if (profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported && !eligibility.CanInstall)
            return new(GameSummary(eligibility.Explanation), eligibility.Explanation, false);
        if (report is null)
            return new("Checking compatibility", "The correct official files and available versions are being checked in the background.", false);
        if (!report.ArtifactResolution.IsFullyAutomatic)
            return new(
                report.ArtifactResolution.MetadataState == MetadataCheckState.UnableToCheck ? "Unable to check required files" : "Required files unavailable",
                report.ArtifactResolution.Warnings.FirstOrDefault() ?? "The complete setup cannot be resolved automatically.",
                false);
        if (report.Components.Any(x => x.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or
            ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable))
            return new("Repair recommended", "Some files from an earlier installation need to be repaired before continuing.", report.CanInstallRecommendedStack);
        if (hasUpdates)
            return new("Update available", "New official files are available for installed components. Review the update plan before applying it.", report.CanInstallRecommendedStack);
        else if (report.Components.Any(x => x.Health == ComponentHealth.Installed))
            return new("Already installed", "The managed compatibility setup is installed and its files are consistent.", report.CanInstallRecommendedStack);
        var alternative = proxy.Candidates.Any(x => !x.SafeForNewInstallation && File.Exists(x.Path));
        return new(
            eligibility.Level == OptiScalerCompatibilityLevel.Experimental &&
                profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported ? "Experimental setup" :
                alternative ? "Compatible method found" : "Ready to install",
            alternative ? "A compatible installation method was found. Existing game files will not be changed." :
                profile.Profile.RenoDxSupport == GameProfileSupport.Unsupported
                ? eligibility.Explanation
                : profile.Profile.RenoDxSupport == GameProfileSupport.EngineFallback
                ? $"RHI Linux found a supported {profile.Profile.Engine} fallback. ReShade and the required game files will be downloaded and configured automatically."
                : "RHI Linux found the correct RenoDX setup for this game. ReShade and required compatibility files will be downloaded and configured automatically.",
            report.CanInstallRecommendedStack);
    }

    private static PrimaryActionKind DeterminePrimaryAction(
        IReadOnlyList<ComponentStatus> components,
        bool hasUpdates)
    {
        var needsRepair = components.Any(x => x.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled or
            ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable);
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
        var reno = report.ArtifactResolution.CanAcquireRenoSetup &&
            ShouldTargetComponent(components, ComponentKind.RenoDx, action) && HasSafeRenoDependency(components);
        var reshade = action is PrimaryActionKind.Update or PrimaryActionKind.Repair &&
            ShouldTargetComponent(components, ComponentKind.ReShade, action) &&
            report.ArtifactResolution.Artifacts.Any(x => x.Component == ComponentKind.ReShade);
        var opti = report.OptiScalerEligibility.CanInstall && report.ArtifactResolution.CanAcquireOptiScaler &&
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
        PathsEqual(plan.GameRoot, snapshot.Game.GameRoot) &&
        PathsEqual(plan.DeploymentDirectory, snapshot.Game.DeploymentDirectory);

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.Ordinal);

    private static DeploymentPlan ClonePlanWithAction(DeploymentPlan plan, PrimaryActionKind action) => new()
    {
        Id = plan.Id,
        AppId = plan.AppId,
        GameRoot = plan.GameRoot,
        DeploymentDirectory = plan.DeploymentDirectory,
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
        foreach (var property in new[] { nameof(SelectedGame), nameof(ComponentCards), nameof(HasSelection), nameof(ShowGame), nameof(IsSelectionLoading), nameof(ShowGameLoading), nameof(HasWarnings), nameof(WarningText), nameof(SelectedGameTitle), nameof(SelectedGameSubtitle), nameof(ExecutableDisplay), nameof(ExecutablePath), nameof(DeploymentDisplay), nameof(DeploymentPath), nameof(ProtonPrefixDisplay), nameof(ProtonPrefixPath), nameof(ConfidenceText), nameof(SelectionReason), nameof(Candidates), nameof(LaunchOption), nameof(ProfileSummary), nameof(SelectedProxy), nameof(DependencySummary), nameof(SupportTitle), nameof(SupportMessage), nameof(CanInstallRecommendedStack), nameof(CanCheckForUpdates), nameof(IsCheckingUpdates), nameof(UpdateState), nameof(UpdateStatusText), nameof(HasUpdates), nameof(PrimaryAction), nameof(PrimaryActionText), nameof(SelectionProgress), nameof(PreviousOperationResult) }) OnPropertyChanged(property);
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
