using RhiLinux.Core;
using RhiLinux.Mods;
using RhiLinux.Sources;
using RhiLinux.Steam;

namespace RhiLinux.Gui;

public interface IGameDiscovery
{
    Task<ScanResult> ScanAsync(IReadOnlyDictionary<string, GameOverride> overrides, CancellationToken cancellationToken);
}

public interface IComponentStatusProvider
{
    Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken);
}

public interface IStackStatusProvider
{
    Task<StackStatusReport> GetAsync(
        InstalledGame game,
        bool allowNetwork,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}

public sealed class GameDiscoveryAdapter : IGameDiscovery
{
    private readonly SteamDiscoveryService service;
    private readonly IReadOnlyList<string>? steamRoots;
    private readonly MultiSourceLibraryService? multiSource;
    private readonly XdgPaths paths;
    private readonly LibraryCoordinator? coordinator;
    private readonly string homeDirectory;

    public GameDiscoveryAdapter(
        SteamDiscoveryService service,
        IReadOnlyList<string>? steamRoots = null,
        ILibraryIndexStore? libraryIndexStore = null,
        MultiSourceLibraryService? multiSource = null,
        XdgPaths? paths = null)
    {
        this.service = service;
        this.steamRoots = steamRoots;
        this.multiSource = multiSource;
        LibraryIndexStore = libraryIndexStore;
        this.paths = paths ?? new XdgPaths();
        homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public GameDiscoveryAdapter(
        LibraryCoordinator coordinator,
        XdgPaths paths,
        IReadOnlyList<string>? steamRoots = null,
        string? homeDirectory = null)
    {
        this.coordinator = coordinator;
        this.paths = paths;
        this.steamRoots = steamRoots;
        service = new SteamDiscoveryService();
        this.homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public IReadOnlyList<string>? ExtraSteamRoots { get; set; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? CustomRootsByProvider { get; set; }
    public ILibraryIndexStore? LibraryIndexStore { get; set; }
    public bool ForceFullAnalysis { get; set; }
    public bool UseAllSources { get; set; } = true;
    public IReadOnlySet<string>? EnabledProviders { get; set; }
    public IncrementalScanMetrics? LastMetrics => service.LastMetrics;
    public MultiSourceScanResult? LastMultiSourceResult { get; private set; }
    public LibrarySnapshot? LastSnapshot { get; private set; }
    public IReadOnlyList<SourceDiagnostic> LastSourceDiagnostics { get; private set; } = [];
    public bool WatchSources { get; set; } = true;

    public async Task RebuildCacheAsync(CancellationToken cancellationToken = default)
    {
        if (coordinator is null) throw new InvalidOperationException("The SQLite library coordinator is not configured.");
        LastSnapshot = (await coordinator.RebuildCacheAsync(cancellationToken).ConfigureAwait(false)).Snapshot;
        LastSourceDiagnostics = LastSnapshot.Diagnostics;
    }

    public async Task<ScanResult> ScanAsync(IReadOnlyDictionary<string, GameOverride> overrides, CancellationToken cancellationToken)
    {
        if (coordinator is not null)
        {
            var customRoots = new List<string>();
            if (steamRoots is not null) customRoots.AddRange(steamRoots);
            if (ExtraSteamRoots is not null) customRoots.AddRange(ExtraSteamRoots);
            var context = SourceRootDiscovery.CreateContext(
                homeDirectory,
                enabledProviders: EnabledProviders,
                customRootsByProvider: BuildCustomRoots(customRoots));
            coordinator.Configure(context, EnabledProviders, overrides);
            coordinator.SetWatchingEnabled(WatchSources);
            var refresh = await coordinator.RefreshAsync(
                LibraryRefreshScope.Full(ForceFullAnalysis ? "Forced GUI refresh" : "GUI refresh", ForceFullAnalysis),
                cancellationToken).ConfigureAwait(false);
            LastSnapshot = refresh.Snapshot;
            LastSourceDiagnostics = refresh.Snapshot.Diagnostics;
            var warnings = refresh.Snapshot.Diagnostics
                .Where(diagnostic => diagnostic.Severity is SourceDiagnosticSeverity.Warning or SourceDiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new(refresh.Snapshot.Games, [], [], warnings);
        }

        if (UseAllSources && multiSource is not null)
        {
            var customRoots = new List<string>();
            if (steamRoots is not null) customRoots.AddRange(steamRoots);
            if (ExtraSteamRoots is not null) customRoots.AddRange(ExtraSteamRoots);
            var discoveryContext = SourceRootDiscovery.CreateContext(
                enabledProviders: EnabledProviders,
                customRootsByProvider: BuildCustomRoots(customRoots));
            var multi = await multiSource.ScanAsync(
                new MultiSourceScanRequest(
                    DiscoveryContext: discoveryContext,
                    EnabledProviders: EnabledProviders,
                    ForceFullScan: ForceFullAnalysis,
                    SourceIndexPath: paths.SourceIndexFile,
                    LibraryDatabasePath: paths.LibraryDatabaseFile),
                cancellationToken).ConfigureAwait(false);
            LastMultiSourceResult = multi;
            LastSourceDiagnostics = multi.Diagnostics;
            var games = ApplyOverrides(multi.Games, overrides);
            var warnings = multi.Diagnostics
                .Where(diagnostic => diagnostic.Severity is SourceDiagnosticSeverity.Warning or SourceDiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var roots = multi.ProviderResults
                .Where(result => result.ProviderId == SteamGameSourceProvider.ProviderId && result.Root.Exists)
                .Select(result => result.Root.CanonicalPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new ScanResult(games, roots, roots, warnings);
        }

        var rootsOnly = new List<string>();
        if (steamRoots is not null) rootsOnly.AddRange(steamRoots);
        if (ExtraSteamRoots is not null) rootsOnly.AddRange(ExtraSteamRoots);
        var includeDefaults = rootsOnly.Count > 0;
        var result = await service.ScanAsync(
            rootsOnly.Count == 0 ? null : rootsOnly,
            ToSteamAppOverrides(overrides),
            cancellationToken,
            includeDefaultRoots: includeDefaults,
            new ScanOptions(
                UseLibraryIndex: LibraryIndexStore is not null,
                ForceFullAnalysis: ForceFullAnalysis,
                LibraryIndexStore: LibraryIndexStore)).ConfigureAwait(false);
        return result with { Games = ApplyOverrides(result.Games, overrides) };
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildCustomRoots(IReadOnlyList<string> steamRoots)
    {
        var result = CustomRootsByProvider is null
            ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            : CustomRootsByProvider.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (steamRoots.Count > 0) result[SteamGameSourceProvider.ProviderId] = steamRoots;
        return result;
    }

    private static IReadOnlyDictionary<uint, GameOverride>? ToSteamAppOverrides(
        IReadOnlyDictionary<string, GameOverride> overrides)
    {
        if (overrides.Count == 0) return null;
        var mapped = new Dictionary<uint, GameOverride>();
        foreach (var (key, value) in overrides)
        {
            if (GameInstallId.TryParse(key, out var installId) &&
                GameInstallId.TryParseSteamAppId(installId, out var steamAppId))
                mapped[steamAppId] = value;
            else if (uint.TryParse(key, out var legacyAppId) && legacyAppId != 0)
                mapped[legacyAppId] = value;
        }
        return mapped.Count == 0 ? null : mapped;
    }

    private static IReadOnlyList<InstalledGame> ApplyOverrides(
        IReadOnlyList<InstalledGame> games,
        IReadOnlyDictionary<string, GameOverride> overrides)
    {
        if (overrides.Count == 0) return games;
        return games.Select(game =>
        {
            if (!overrides.TryGetValue(game.EffectiveInstallId, out var gameOverride) &&
                !(game.SteamAppId is { } steamAppId &&
                  (overrides.TryGetValue(steamAppId.ToString(), out gameOverride) ||
                   overrides.TryGetValue(GameInstallId.LegacySteam(steamAppId).Value, out gameOverride))))
                return game;
            var executable = gameOverride.Executable ?? game.Executable;
            var deployment = gameOverride.DeploymentDirectory ??
                (executable is null ? game.DeploymentDirectory : Path.GetDirectoryName(executable) ?? game.DeploymentDirectory);
            return game with
            {
                Executable = executable,
                DeploymentDirectory = deployment,
                Prefix = gameOverride.Prefix ?? game.Prefix,
                SelectionReason = gameOverride.Executable is not null
                    ? "Selected because a persistent manual executable override is configured."
                    : game.SelectionReason,
                Confidence = gameOverride.Executable is not null ? DetectionConfidence.High : game.Confidence
            };
        }).ToArray();
    }
}

public sealed class ComponentStatusProviderAdapter(ComponentDetector detector) : IComponentStatusProvider
{
    public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken) =>
        detector.DetectAsync(game.ToDeploymentTarget(), cancellationToken);
}

public sealed class StackStatusProviderAdapter(StackStatusService service) : IComponentStatusProvider
{
    public async Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken) =>
        (await service.GetAsync(game.ToDeploymentTarget(), false, cancellationToken)).Components;
}

public sealed class StackReportProviderAdapter(StackStatusService service) : IStackStatusProvider
{
    public Task<StackStatusReport> GetAsync(
        InstalledGame game,
        bool allowNetwork,
        CancellationToken cancellationToken,
        bool forceRefresh = false) => service.GetAsync(game.ToDeploymentTarget(), allowNetwork, cancellationToken, forceRefresh);
}
