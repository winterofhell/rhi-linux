using System.ComponentModel;
using System.Runtime.CompilerServices;
using RhiLinux.Core;

namespace RhiLinux.Gui;

public abstract class PageViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(propertyName);
        return true;
    }
}

public sealed class OverviewViewModel : PageViewModel
{
    private int gameCount;
    private int readyCount;
    private int needsAttentionCount;
    private int unsupportedCount;
    private int updateCount;
    private int recoveryCount;
    private int providerWarningCount;

    public int GameCount { get => gameCount; private set => Set(ref gameCount, value); }
    public int ReadyCount { get => readyCount; private set => Set(ref readyCount, value); }
    public int NeedsAttentionCount { get => needsAttentionCount; private set => Set(ref needsAttentionCount, value); }
    public int UnsupportedCount { get => unsupportedCount; private set => Set(ref unsupportedCount, value); }
    public int UpdateCount { get => updateCount; private set => Set(ref updateCount, value); }
    public int RecoveryCount { get => recoveryCount; private set => Set(ref recoveryCount, value); }
    public int ProviderWarningCount { get => providerWarningCount; private set => Set(ref providerWarningCount, value); }
    public string Summary => $"{GameCount} games · {ReadyCount} ready · {NeedsAttentionCount} need attention";

    public void Update(
        IReadOnlyList<InstalledGame> games,
        IReadOnlyDictionary<string, GameReadinessState> readiness,
        int updates,
        int warnings)
    {
        GameCount = games.Count;
        ReadyCount = readiness.Count(pair => pair.Value is GameReadinessState.Ready or GameReadinessState.ReadyWithWarnings);
        NeedsAttentionCount = readiness.Count(pair => pair.Value is GameReadinessState.NeedsConfiguration or
            GameReadinessState.NeedsUserSelection or GameReadinessState.Error);
        UnsupportedCount = readiness.Count(pair => pair.Value is GameReadinessState.Unsupported or GameReadinessState.Unavailable);
        UpdateCount = updates;
        RecoveryCount = games.Count(game => DeploymentRecoveryProbe.Probe(game.GameRoot).HasInterruptedTransaction);
        ProviderWarningCount = warnings;
        Changed(nameof(Summary));
    }
}

public sealed class LibraryViewModel : PageViewModel
{
    private IReadOnlyList<InstalledGame> games = [];

    public IReadOnlyList<InstalledGame> Games { get => games; private set => Set(ref games, value); }
    public int Count => Games.Count;

    public void Apply(
        IReadOnlyList<InstalledGame> source,
        string filter,
        string search,
        bool showUnsupportedNativeGames,
        IReadOnlyDictionary<string, GameReadinessState> readiness,
        IReadOnlySet<GameInstallId>? updateable = null)
    {
        IEnumerable<InstalledGame> selected = filter switch
        {
            "Steam" => source.Where(game => game.Store == GameStore.Steam || game.Launcher == GameLauncher.Steam),
            "Epic" => source.Where(game => game.Store == GameStore.Epic),
            "GOG" => source.Where(game => game.Store == GameStore.Gog),
            "Amazon" => source.Where(game => game.Store == GameStore.Amazon),
            "Heroic" => source.Where(game => game.Launcher == GameLauncher.Heroic),
            "Legendary" => source.Where(game => game.Launcher == GameLauncher.Legendary),
            "Lutris" => source.Where(game => game.Launcher == GameLauncher.Lutris),
            "Bottles" => source.Where(game => game.Launcher == GameLauncher.Bottles),
            "Minigalaxy" => source.Where(game => game.Launcher == GameLauncher.Minigalaxy),
            "Manual" => source.Where(game => game.Launcher == GameLauncher.Manual),
            "Windows" => source.Where(game => game.Platform == GameBinaryPlatform.Windows && game.IsActionable),
            "Native/unsupported" => source.Where(game => game.Platform == GameBinaryPlatform.Linux || game.IsNativeLinux || !game.IsActionable),
            "Needs executable confirmation" => source.Where(game => game.RequiresConfirmation || game.Executable is null ||
                game.Confidence is DetectionConfidence.None or DetectionConfidence.Low),
            "Not configured" => source.Where(game => game.Executable is null ||
                game.Confidence is DetectionConfidence.None or DetectionConfidence.Low),
            "Anti-cheat" => source.Where(game => game.RequiresConfirmation),
            "Unsupported" => source.Where(game => !game.IsActionable || game.IsNativeLinux || game.Executable is null),
            "Unreal" => source.Where(game => game.Engine is GameEngine.Unreal or GameEngine.UnrealLegacy),
            "Unity" => source.Where(game => game.Engine == GameEngine.Unity),
            "RE Engine" => source.Where(game => game.Engine == GameEngine.ReEngine),
            "Installing" => source.Where(game => game.InstallState == SteamInstallState.Installing),
            "Ready" => source.Where(game => readiness.GetValueOrDefault(game.EffectiveInstallId) is
                GameReadinessState.Ready or GameReadinessState.ReadyWithWarnings),
            "Needs attention" => source.Where(game => readiness.GetValueOrDefault(game.EffectiveInstallId) is
                GameReadinessState.NeedsConfiguration or GameReadinessState.NeedsUserSelection or GameReadinessState.Error),
            "Recovery required" => source.Where(game => DeploymentRecoveryProbe.Probe(game.GameRoot).HasInterruptedTransaction),
            _ => source
        };
        if (!showUnsupportedNativeGames)
            selected = selected.Where(game => !game.IsNativeLinux && game.Platform != GameBinaryPlatform.Linux);
        Games = GameSearch.Rank(selected, search, readiness, updateable);
        Changed(nameof(Count));
    }
}

public sealed class UpdatesViewModel : PageViewModel
{
    private IReadOnlyList<ComponentUpdateItemViewModel> items = [];
    private UpdateCheckState state = UpdateCheckState.UnableToCheck;
    private string status = string.Empty;

    public IReadOnlyList<ComponentUpdateItemViewModel> Items { get => items; private set => Set(ref items, value); }
    public UpdateCheckState State { get => state; private set => Set(ref state, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool HasAvailable => Items.Count > 0 || State == UpdateCheckState.UpdateAvailable;

    public void Update(IReadOnlyList<ComponentUpdateItemViewModel> updates, UpdateCheckState updateState, string statusText)
    {
        Items = updates;
        State = updateState;
        Status = statusText;
        Changed(nameof(HasAvailable));
    }
}

public sealed class DiagnosticsViewModel : PageViewModel
{
    private IReadOnlyList<DiagnosticItemViewModel> items = [];
    private IReadOnlyList<ProviderStatusRowViewModel> providers = [];
    private LibraryMetrics metrics = LibraryMetrics.Empty;

    public IReadOnlyList<DiagnosticItemViewModel> Items { get => items; private set => Set(ref items, value); }
    public IReadOnlyList<ProviderStatusRowViewModel> Providers { get => providers; private set => Set(ref providers, value); }
    public LibraryMetrics Metrics { get => metrics; private set => Set(ref metrics, value); }
    public string LastRefresh =>
        $"Reason: {Metrics.Reason} · Providers: {string.Join(", ", Metrics.ProvidersExecuted)} · " +
        $"Parsed: {Metrics.SourceDocumentsParsed} · Reconciled: {Metrics.InstallationsReconciled} · " +
        $"Analyzed: {Metrics.InstallationsAnalyzed} · {Metrics.TotalDuration.TotalMilliseconds:0} ms";

    public void Update(
        IEnumerable<SourceDiagnostic> diagnostics,
        IReadOnlyList<ProviderStatusRowViewModel> providerRows,
        LibraryMetrics refreshMetrics)
    {
        Items = diagnostics.OrderByDescending(item => item.Severity)
            .ThenBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DiagnosticItemViewModel(item))
            .ToArray();
        Providers = providerRows;
        Metrics = refreshMetrics;
        Changed(nameof(LastRefresh));
    }
}

public sealed class SettingsViewModel : PageViewModel
{
    private int enabledSourceCount;
    private double cacheLimitGiB;

    public int EnabledSourceCount { get => enabledSourceCount; private set => Set(ref enabledSourceCount, value); }
    public double CacheLimitGiB { get => cacheLimitGiB; private set => Set(ref cacheLimitGiB, value); }

    public void Update(UiPreferences preferences)
    {
        EnabledSourceCount = preferences.ScanAllSources
            ? 2 + new[]
            {
                preferences.EnableHeroic,
                preferences.EnableLegendary,
                preferences.EnableLutris,
                preferences.EnableBottles,
                preferences.EnableMinigalaxy
            }.Count(enabled => enabled)
            : 1;
        CacheLimitGiB = preferences.CacheLimitMiB / 1024d;
    }
}

public sealed class GameDetailsViewModel : PageViewModel
{
    private InstalledGame? game;
    private IReadOnlyList<ComponentCardViewModel> components = [];
    private GameReadinessResult? readiness;

    public InstalledGame? Game { get => game; private set => Set(ref game, value); }
    public IReadOnlyList<ComponentCardViewModel> Components { get => components; private set => Set(ref components, value); }
    public GameReadinessResult? Readiness { get => readiness; private set => Set(ref readiness, value); }
    public bool IsLoading { get; private set; }

    public void Update(GameSelectionSnapshot snapshot, GameReadinessResult? result)
    {
        Game = snapshot.Game;
        Components = snapshot.ComponentCards;
        Readiness = result;
        IsLoading = snapshot.IsLoading;
        Changed(nameof(IsLoading));
    }

    public void Clear()
    {
        Game = null;
        Components = [];
        Readiness = null;
        IsLoading = false;
        Changed(nameof(IsLoading));
    }
}
