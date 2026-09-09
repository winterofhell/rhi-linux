using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class GuiViewModelTests
{
    [Fact]
    public async Task FiltersByStoreAndLauncherBadges()
    {
        var discovery = new FakeDiscovery(
        [
            Game(10, "Steam Game") with { Store = GameStore.Steam, PrimaryLauncher = GameLauncher.Steam, ExternalId = "10" },
            Game(20, "Heroic Epic") with { Store = GameStore.Epic, PrimaryLauncher = GameLauncher.Heroic, ExternalId = "Control" },
            Game(25, "Legendary Epic") with { Store = GameStore.Epic, PrimaryLauncher = GameLauncher.Legendary, ExternalId = "Control" },
            Game(28, "Minigalaxy Gog") with { Store = GameStore.Gog, PrimaryLauncher = GameLauncher.Minigalaxy, ExternalId = "456" },
            Game(30, "Lutris Gog") with { Store = GameStore.Gog, PrimaryLauncher = GameLauncher.Lutris, ExternalId = "123", IsActionable = false, UnsupportedReason = "Native" , Platform = GameBinaryPlatform.Linux }
        ]);
        var viewModel = Create(discovery);
        await viewModel.InitializeAsync();

        Assert.Equal("Steam", viewModel.Games.Single(game => game.AppId == 10).IdentitySummary);

        viewModel.LibraryFilter = "Heroic";
        Assert.Equal("Heroic Epic", Assert.Single(viewModel.FilteredGames).Name);
        viewModel.LibraryFilter = "Legendary";
        Assert.Equal("Legendary Epic", Assert.Single(viewModel.FilteredGames).Name);
        viewModel.LibraryFilter = "Minigalaxy";
        Assert.Equal("Minigalaxy Gog", Assert.Single(viewModel.FilteredGames).Name);
        viewModel.LibraryFilter = "GOG";
        Assert.Equal(2, viewModel.FilteredGames.Count);
        viewModel.LibraryFilter = "Native/unsupported";
        Assert.Equal("Lutris Gog", Assert.Single(viewModel.FilteredGames).Name);
        Assert.Contains("GOG", viewModel.FilteredGames[0].IdentitySummary, StringComparison.Ordinal);
        Assert.Contains("Lutris", viewModel.FilteredGames[0].IdentitySummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigationSelectsExactlyOnePageAndPreservesStateAcrossRefresh()
    {
        var discovery = new FakeDiscovery([Game(10, "First"), Game(20, "Second")]);
        var viewModel = Create(discovery);
        await viewModel.InitializeAsync();
        var scansAfterInit = discovery.ScanCount;

        viewModel.Navigate(MainSection.Overview);
        Assert.Equal(MainSection.Overview, viewModel.SelectedSection);
        Assert.True(viewModel.ShowOverviewPage);
        Assert.False(viewModel.ShowLibraryPage);
        Assert.False(viewModel.ShowUpdatesPage);
        Assert.False(viewModel.ShowDiagnosticsPage);
        Assert.False(viewModel.ShowSettingsPage);

        viewModel.Navigate(MainSection.Updates);
        Assert.True(viewModel.ShowUpdatesPage);
        Assert.False(viewModel.ShowLibraryPage);
        Assert.True(viewModel.UpdatesIsEmpty || viewModel.UpdatesIsUnavailable || viewModel.UpdatesHasAvailable || viewModel.UpdatesIsLoading || viewModel.UpdatesIsOffline);

        viewModel.Navigate(MainSection.Diagnostics);
        Assert.True(viewModel.ShowDiagnosticsPage);
        Assert.False(viewModel.ShowOverviewPage);

        viewModel.Navigate(MainSection.Settings);
        Assert.True(viewModel.ShowSettingsPage);
        Assert.False(viewModel.ShowDiagnosticsPage);

        viewModel.Navigate(MainSection.Library);
        Assert.True(viewModel.ShowLibraryPage);
        Assert.False(viewModel.ShowSettingsPage);

        viewModel.LibraryFilter = "Steam";
        viewModel.Navigate(MainSection.Overview);
        await viewModel.RefreshAsync();
        Assert.Equal(MainSection.Overview, viewModel.SelectedSection);
        Assert.Equal("Steam", viewModel.LibraryFilter);
        Assert.True(discovery.ScanCount > scansAfterInit);
        Assert.False(viewModel.ShowWelcome);
        Assert.False(viewModel.ShowNoGames);
    }

    [Fact]
    public async Task OverviewEvaluatesReadinessAcrossEntireLibrary()
    {
        var ready = Game(10, "Ready");
        var attention = Game(20, "Attention");
        var unsupported = Game(30, "Unsupported");
        var readiness = new FixedReadinessService(new Dictionary<uint, GameReadinessState>
        {
            [ready.AppId] = GameReadinessState.Ready,
            [attention.AppId] = GameReadinessState.NeedsConfiguration,
            [unsupported.AppId] = GameReadinessState.Unsupported
        });
        var viewModel = Create(new FakeDiscovery([ready, attention, unsupported]), readinessService: readiness);

        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.OverviewReadinessPendingCount == 0);

        Assert.Equal(1, viewModel.OverviewReadyCount);
        Assert.Equal(1, viewModel.OverviewNeedsAttentionCount);
        Assert.Equal(1, viewModel.OverviewUnsupportedReadinessCount);
        Assert.Equal(3, readiness.EvaluationCount);
    }

    [Fact]
    public async Task SwitchingGamesClearsPreviousGamesUpdateRowsImmediately()
    {
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var secondResult = new TaskCompletionSource<GameReadinessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readiness = new FixedReadinessService(new Dictionary<uint, GameReadinessState>
        {
            [first.AppId] = GameReadinessState.Ready
        }, new Dictionary<uint, Task<GameReadinessResult>>
        {
            [second.AppId] = secondResult.Task
        }, outdatedAppId: first.AppId);
        var viewModel = Create(new FakeDiscovery([first, second]), readinessService: readiness);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.ComponentUpdates.Count > 0);

        await viewModel.SelectAsync(second);

        Assert.Empty(viewModel.ComponentUpdates);
        Assert.Null(viewModel.Readiness.Result);
        secondResult.SetResult(ReadinessResult(second, GameReadinessState.Ready, ComponentHealth.Available));
        await WaitUntilAsync(() => !viewModel.IsEvaluatingLibraryReadiness);
    }

    [Fact]
    public async Task FilterChangesDoNotTriggerSourceScanAndSurviveLibraryUpdates()
    {
        var discovery = new FakeDiscovery([Game(10, "Steam Game"), Game(20, "Heroic Epic") with
        {
            Store = GameStore.Epic,
            PrimaryLauncher = GameLauncher.Heroic,
            ExternalId = "Control"
        }]);
        var viewModel = Create(discovery);
        await viewModel.InitializeAsync();
        var scansAfterInit = discovery.ScanCount;
        viewModel.Navigate(MainSection.Diagnostics);

        foreach (var filter in new[]
                 {
                     "All", "Steam", "Epic", "GOG", "Amazon", "Heroic", "Legendary", "Lutris", "Bottles",
                     "Minigalaxy", "Manual"
                 })
        {
            viewModel.LibraryFilter = filter;
        }

        Assert.Equal(scansAfterInit, discovery.ScanCount);
        Assert.Equal(MainSection.Diagnostics, viewModel.SelectedSection);

        discovery.Games = [Game(10, "Steam Game updated"), Game(30, "Third")];
        await viewModel.RefreshAsync();
        Assert.Equal(MainSection.Diagnostics, viewModel.SelectedSection);
        Assert.Equal("Manual", viewModel.LibraryFilter);
    }

    [Fact]
    public async Task ProviderTogglePersistsAndSchedulesRefresh()
    {
        var discovery = new FakeDiscovery([Game(10, "Steam Game")]);
        var preferences = new MemoryPreferencesStore(new UiPreferences { EnableHeroic = true, ScanAllSources = true });
        var viewModel = Create(discovery, preferences);
        await viewModel.InitializeAsync();
        var scansAfterInit = discovery.ScanCount;

        viewModel.EnableHeroic = false;
        Assert.False(preferences.Saved?.EnableHeroic);
        await Task.Delay(400);
        Assert.True(discovery.ScanCount > scansAfterInit);

        var scansAfterDisable = discovery.ScanCount;
        viewModel.EnableLutris = false;
        Assert.False(preferences.Saved?.EnableLutris);
        await Task.Delay(400);
        Assert.True(discovery.ScanCount > scansAfterDisable);

        viewModel.EnableHeroic = true;
        Assert.True(preferences.Saved?.EnableHeroic);
    }

    [Fact]
    public async Task EmptyLibraryShowsOverviewWithoutOverlappingLibraryEmptyState()
    {
        var viewModel = Create(new FakeDiscovery([]));
        await viewModel.InitializeAsync();

        viewModel.Navigate(MainSection.Overview);
        Assert.True(viewModel.ShowOverviewPage);
        Assert.False(viewModel.ShowNoGames);
        Assert.False(viewModel.ShowWelcome);
        Assert.Equal(0, viewModel.OverviewGameCount);
        Assert.Contains("0 games", viewModel.OverviewSummary, StringComparison.Ordinal);

        viewModel.Navigate(MainSection.Library);
        Assert.True(viewModel.ShowNoGames);
        Assert.False(viewModel.ShowOverviewPage);
    }

    [Fact]
    public async Task FiltersByNameAppIdAndEngine()
    {
        var discovery = new FakeDiscovery([Game(10, "Unreal Quest", GameEngine.Unreal), Game(20, "Small World", GameEngine.Unity)]);
        var viewModel = Create(discovery);
        await viewModel.InitializeAsync();

        viewModel.SearchText = "unity";
        Assert.Single(viewModel.FilteredGames);
        Assert.Equal(20u, viewModel.FilteredGames[0].AppId);
        viewModel.SearchText = "10";
        Assert.Equal(10u, Assert.Single(viewModel.FilteredGames).AppId);
        viewModel.SearchText = "world";
        Assert.Equal(20u, Assert.Single(viewModel.FilteredGames).AppId);
    }

    [Fact]
    public async Task RestoresAndRetainsSelectionAcrossRefresh()
    {
        var discovery = new FakeDiscovery([Game(10, "First"), Game(20, "Second")]);
        var preferences = new MemoryPreferencesStore(new UiPreferences { SelectedAppId = 20 });
        var viewModel = Create(discovery, preferences);
        await viewModel.InitializeAsync();
        Assert.Equal(20u, viewModel.SelectedGame?.AppId);

        discovery.Games = [Game(20, "Second updated"), Game(30, "Third")];
        await viewModel.RefreshAsync();
        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.Equal("Second updated", viewModel.SelectedGame?.Name);
    }

    [Fact]
    public async Task CancelledRefreshClearsTheSelectionLoadingIndicator()
    {
        var discovery = new ControlledDiscovery();
        var viewModel = new MainViewModel(discovery, new FakeStatusProvider(), new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()));
        await viewModel.SelectAsync(Game(10, "First"));

        var refresh = viewModel.RefreshAsync();
        await discovery.WaitForRequestAsync();
        Assert.True(viewModel.IsSelectionLoading);

        discovery.Cancel();
        await refresh;

        Assert.False(viewModel.IsSelectionLoading);
    }

    [Fact]
    public void ReadinessShortcutFiltersAreSelectableLibraryFilters()
    {
        var viewModel = new MainViewModel(new FakeDiscovery([]), new FakeStatusProvider(), new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()));

        foreach (var filter in new[] { "Ready", "Needs attention", "Recovery required" })
        {
            Assert.Contains(filter, viewModel.LibraryFilters);
            viewModel.FilterLibraryForReadiness(filter);
            Assert.Equal(filter, viewModel.LibraryFilter);
        }
    }

    [Fact]
    public async Task StartupReconcilesStalePersistedGameList()
    {
        var stale = PersistedGameEntry.FromInstalledGame(Game(10, "Uninstalled"));
        var current = Game(20, "Current");
        var stateStore = new MemoryStateStore(new ApplicationState { DiscoveredGames = [stale] });
        var viewModel = new MainViewModel(new FakeDiscovery([current]), new FakeStatusProvider(), stateStore,
            new MemoryPreferencesStore(new UiPreferences { SelectedAppId = 10 }));

        await viewModel.InitializeAsync();

        Assert.Equal(20u, Assert.Single(viewModel.Games).AppId);
        Assert.Equal(20u, Assert.Single(stateStore.State.DiscoveredGames).SteamAppId);
        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
    }

    [Fact]
    public async Task FullRefreshUpdatesGameListAndReloadsSelectedComponentState()
    {
        var selected = Game(20, "Selected");
        var discovery = new FakeDiscovery([Game(10, "Old"), selected]);
        var statuses = new CountingStatusProvider();
        var preferences = new MemoryPreferencesStore(new UiPreferences { SelectedAppId = 20 });
        var viewModel = Create(discovery, preferences, statuses);
        await viewModel.InitializeAsync();
        var initialStatusScans = statuses.Count;

        discovery.Games = [selected with { Name = "Selected updated" }, Game(30, "New")];
        await viewModel.RefreshAsync();

        Assert.DoesNotContain(viewModel.Games, game => game.AppId == 10);
        Assert.Contains(viewModel.Games, game => game.AppId == 30);
        Assert.Equal("2", viewModel.FilteredGameCount);
        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.Equal("Selected updated", viewModel.SelectedGame?.Name);
        Assert.True(statuses.Count > initialStatusScans);
        Assert.Contains($"scan {statuses.Count}", viewModel.ComponentCards[0].TechnicalExplanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshShowsGameWithoutExecutableOrProtonPrefix()
    {
        var pending = Game(40, "Not launched yet") with
        {
            Executable = null,
            Confidence = DetectionConfidence.None,
            SelectionReason = "No Windows executable found; a Proton prefix may become available later.",
            Prefix = null
        };
        var discovery = new FakeDiscovery([]);
        var viewModel = Create(discovery);
        await viewModel.InitializeAsync();

        discovery.Games = [pending];
        await viewModel.RefreshAsync();

        Assert.Equal(40u, Assert.Single(viewModel.Games).AppId);
        Assert.Equal("Not detected", viewModel.ExecutableDisplay);
        Assert.Equal("Proton prefix not created yet", viewModel.ProtonPrefixDisplay);
    }

    [Fact]
    public async Task ReportsNoGamesAndErrorStates()
    {
        var discovery = new FakeDiscovery([]);
        var viewModel = Create(discovery);
        await viewModel.InitializeAsync();
        Assert.Equal(UiState.NoGames, viewModel.CurrentState);
        Assert.True(viewModel.ShowNoGames);

        discovery.Error = new IOException("fixture failure");
        await viewModel.RefreshAsync();
        Assert.Equal(UiState.Error, viewModel.CurrentState);
        Assert.Contains("fixture failure", viewModel.ErrorMessage);
    }

    [Fact]
    public void ComponentActionsReflectOwnershipAndHealth()
    {
        var unavailable = Card(ComponentHealth.Available);
        Assert.False(unavailable.CanInstall);
        var cleanReShade = new ComponentCardViewModel(new ComponentStatus(
            ComponentKind.ReShade, ComponentHealth.Available, null, [], "Not installed.",
            Lifecycle: ComponentLifecycleState.NotInstalled));
        Assert.True(cleanReShade.CanInstall);
        Assert.Equal("Install", cleanReShade.ActionText);
        var automatic = Card(ComponentHealth.DownloadRequired, Resolved(ComponentKind.RenoDx));
        Assert.True(automatic.CanInstall);
        Assert.False(unavailable.CanRemove);

        var installed = Card(ComponentHealth.Installed);
        Assert.False(installed.CanUpdate);
        Assert.True(installed.CanRemove);
        Assert.Equal("Remove", installed.ActionText);

        var broken = Card(ComponentHealth.Broken);
        Assert.True(broken.CanRepair);
        Assert.True(broken.CanRemove);
        Assert.True(broken.ShowRemoveAction);
        Assert.Equal("Repair", broken.ActionText);
        Assert.Equal("Remove RenoDX", broken.RemoveActionText);

        var outdated = Card(ComponentHealth.Outdated);
        Assert.True(outdated.CanUpdate);
        Assert.True(outdated.CanRemove);
        Assert.True(outdated.ShowRemoveAction);
        Assert.Equal("Update", outdated.ActionText);

        var foreign = new ComponentCardViewModel(new ComponentStatus(ComponentKind.ReShade, ComponentHealth.Installed, null, ["dxgi.dll"], "Detected on disk but not owned by RHI Linux; removal is disabled.",
            InstallationVerification.RecognizedExisting, Lifecycle: ComponentLifecycleState.InstalledUnmanaged,
            Ownership: OwnershipHealth.Unmanaged, Update: UpdateAvailability.ManualInstallationDetected));
        Assert.True(foreign.CanRemove);
        Assert.False(foreign.CanUpdate);
        Assert.False(foreign.NoActionNeeded);
        Assert.Equal("Installed manually", foreign.State);
        Assert.Equal("Remove", foreign.ActionText);
        Assert.Contains("recovery plan", foreign.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComponentDetailsAreExclusiveAndCollapseWhenGameChanges()
    {
        var viewModel = Create(new FakeDiscovery([Game(10, "First"), Game(20, "Second")]));
        await viewModel.InitializeAsync();
        var first = viewModel.ComponentCards[0];
        var second = viewModel.ComponentCards[1];

        first.IsDetailsExpanded = true;
        second.IsDetailsExpanded = true;

        Assert.False(first.IsDetailsExpanded);
        Assert.True(second.IsDetailsExpanded);
        await viewModel.SelectAsync(viewModel.Games.Single(x => x.AppId == 20));
        Assert.All(viewModel.ComponentCards, card => Assert.False(card.IsDetailsExpanded));
    }

    [Fact]
    public async Task ComponentDetailsPersistForSameGameUntilMaterialStateChanges()
    {
        var statuses = new FakeStatusProvider();
        var game = Game(10, "First");
        var viewModel = Create(new FakeDiscovery([game]), statusProvider: statuses);
        await viewModel.InitializeAsync();
        viewModel.ComponentCards[0].IsDetailsExpanded = true;

        await viewModel.SelectAsync(game);
        Assert.True(viewModel.ComponentCards[0].IsDetailsExpanded);

        statuses.ReShadeHealth = ComponentHealth.Installed;
        await viewModel.SelectAsync(game);
        Assert.False(viewModel.ComponentCards[0].IsDetailsExpanded);
    }

    [Fact]
    public async Task RapidSelectionClearsOldCardsAndIgnoresLateResult()
    {
        var provider = new ControlledStatusProvider();
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var viewModel = Create(new FakeDiscovery([first, second]), statusProvider: provider);

        var firstSelection = viewModel.SelectAsync(first);
        Assert.Empty(viewModel.ComponentCards);
        var secondSelection = viewModel.SelectAsync(second);
        Assert.Empty(viewModel.ComponentCards);

        provider.Complete(20);
        await secondSelection;
        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.All(viewModel.ComponentCards, x => Assert.Contains("20", x.TechnicalExplanation, StringComparison.Ordinal));

        provider.Complete(10);
        await firstSelection;
        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.All(viewModel.ComponentCards, x => Assert.Contains("20", x.TechnicalExplanation, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchingInstalledAndCleanGamesNeverLeaksComponentState()
    {
        var installed = Game(10, "Installed");
        var clean = Game(20, "Clean");
        var provider = new PerGameStatusProvider(new Dictionary<uint, ComponentHealth>
        {
            [10] = ComponentHealth.Installed,
            [20] = ComponentHealth.Available
        });
        var viewModel = Create(new FakeDiscovery([installed, clean]), statusProvider: provider);

        await viewModel.SelectAsync(installed);
        Assert.All(viewModel.ComponentCards, card => Assert.Equal(ComponentHealth.Installed, card.Health));
        await viewModel.SelectAsync(clean);
        Assert.All(viewModel.ComponentCards, card => Assert.Equal(ComponentHealth.Available, card.Health));
        Assert.All(viewModel.ComponentCards, card => Assert.False(card.CanRemove));

        await viewModel.SelectAsync(installed);
        Assert.All(viewModel.ComponentCards, card => Assert.Equal(ComponentHealth.Installed, card.Health));
        Assert.All(viewModel.ComponentCards, card => Assert.True(card.CanRemove));
    }

    [Fact]
    public async Task SelectionImmediatelyPublishesFreshLoadingSnapshot()
    {
        var provider = new ControlledStatusProvider();
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var viewModel = Create(new FakeDiscovery([first, second]), statusProvider: provider);
        var firstSelection = viewModel.SelectAsync(first);
        provider.Complete(10);
        await firstSelection;
        viewModel.ComponentCards[0].IsDetailsExpanded = true;

        var secondSelection = viewModel.SelectAsync(second);

        Assert.True(viewModel.IsSelectionLoading);
        Assert.True(viewModel.ShowGameLoading);
        Assert.Empty(viewModel.ComponentCards);
        Assert.False(viewModel.HasUpdates);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Null(viewModel.PreviousOperationResult);
        Assert.Equal("Looking for a safe compatibility filename…", viewModel.SelectedProxy);
        provider.Complete(20);
        await secondSelection;
        Assert.All(viewModel.ComponentCards, card => Assert.False(card.IsDetailsExpanded));
    }

    [Fact]
    public async Task SelectionCancelsComponentScanAndIgnoresProviderThatCompletesAnyway()
    {
        var provider = new CancellationTrackingStatusProvider();
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var viewModel = Create(new FakeDiscovery([first, second]), statusProvider: provider);
        var firstSelection = viewModel.SelectAsync(first);
        await provider.WaitForRequestAsync(10);

        var secondSelection = viewModel.SelectAsync(second);
        await provider.WaitForRequestAsync(20);
        await provider.WaitForCancellationAsync(10);
        provider.Complete(20, ComponentHealth.Available);
        await secondSelection;
        provider.Complete(10, ComponentHealth.Installed);
        await firstSelection;

        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.All(viewModel.ComponentCards, card => Assert.Equal(ComponentHealth.Available, card.Health));
    }

    [Fact]
    public async Task UpdateAndCachedStateFromOldGameCannotOverwriteNewSelection()
    {
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var updates = new ControlledStackStatusProvider();
        var viewModel = Create(new FakeDiscovery([first, second]), stackStatusProvider: updates);
        await viewModel.SelectAsync(first);
        await updates.WaitForRequestAsync(10);

        await viewModel.SelectAsync(second);
        await updates.WaitForRequestAsync(20);
        await updates.WaitForCancellationAsync(10);
        updates.Complete(second, ComponentHealth.Available, MetadataCheckState.Online, cached: false);
        await WaitUntilAsync(() => !viewModel.IsCheckingUpdates && viewModel.UpdateState == UpdateCheckState.UpToDate);
        updates.Complete(first, ComponentHealth.Outdated, MetadataCheckState.Online, cached: true);
        await Task.Yield();

        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.False(viewModel.HasUpdates);
        Assert.Equal(UpdateCheckState.UpToDate, viewModel.UpdateState);
        Assert.All(viewModel.ComponentCards, card => Assert.False(card.HasCachePath));
    }

    [Fact]
    public async Task RapidAlternatingSelectionStressKeepsNewestSnapshot()
    {
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var provider = new JitterStatusProvider();
        var viewModel = Create(new FakeDiscovery([first, second]), statusProvider: provider);
        var selections = new List<Task>();
        for (var index = 0; index < 100; index++)
            selections.Add(viewModel.SelectAsync(index % 2 == 0 ? first : second));

        await Task.WhenAll(selections);

        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.All(viewModel.ComponentCards, card => Assert.Equal("game 20", card.TechnicalExplanation));
    }

    [Fact]
    public async Task SelectionDuringRefreshRemainsSelectedWhenScanCompletes()
    {
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var discovery = new ControlledDiscovery();
        var viewModel = new MainViewModel(discovery, new FakeStatusProvider(), new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()));
        await viewModel.SelectAsync(first);
        var refresh = viewModel.RefreshAsync();
        await discovery.WaitForRequestAsync();

        await viewModel.SelectAsync(second);
        discovery.Complete([first, second]);
        await refresh;

        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.Equal("Second", viewModel.SelectedGame?.Name);
    }

    [Fact]
    public async Task SwitchingDuringOperationClearsProgressAndIgnoresOldCompletion()
    {
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var executor = new ControlledExecutor();
        var viewModel = new MainViewModel(new FakeDiscovery([first, second]), new FakeStatusProvider(),
            new MemoryStateStore(), new MemoryPreferencesStore(new UiPreferences()), executor: executor);
        await viewModel.SelectAsync(first);
        var plan = new DeploymentPlan
        {
            Id = "fixture",
            InstallId = first.EffectiveInstallId,
            SteamAppId = first.SteamAppId,
            GameRoot = first.GameRoot,
            DeploymentDirectory = first.DeploymentDirectory ?? first.GameRoot,
            Action = "install RenoDx",
            ExpectedComponentStates = [new(ComponentKind.RenoDx, true)]
        };
        var execution = viewModel.ExecuteAsync(plan, false);
        Assert.NotEmpty(viewModel.SelectionProgress);

        await viewModel.SelectAsync(second);
        Assert.Empty(viewModel.SelectionProgress);
        Assert.Null(viewModel.PreviousOperationResult);
        executor.Complete();
        await execution;

        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.Equal(UiState.Ready, viewModel.CurrentState);
        Assert.Null(viewModel.PreviousOperationResult);
    }

    [Fact]
    public async Task ErrorsAndWarningsClearWhenSelectionChanges()
    {
        var first = Game(10, "First") with { RequiresConfirmation = true };
        var second = Game(20, "Second");
        var viewModel = Create(new FakeDiscovery([first, second]), statusProvider: new FailingPerGameStatusProvider(10));

        await viewModel.SelectAsync(first);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.HasWarnings);
        await viewModel.SelectAsync(second);

        Assert.False(viewModel.HasError);
        Assert.False(viewModel.HasWarnings);
        Assert.Empty(viewModel.WarningText);
    }

    [Fact]
    public async Task SuccessfulInstallReloadsAndRequiresDetectedInstalledState()
    {
        var game = Game(10, "First");
        var statuses = new SequenceStatusProvider(ComponentHealth.Available, ComponentHealth.Installed);
        var viewModel = new MainViewModel(new FakeDiscovery([game]), statuses, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new SuccessfulExecutor());
        await viewModel.SelectAsync(game);
        var plan = new DeploymentPlan
        {
            Id = "fixture",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory ?? game.GameRoot,
            Action = "install RenoDx",
            ExpectedComponentStates = [new(ComponentKind.RenoDx, true)]
        };

        var result = await viewModel.ExecuteAsync(plan, false);

        Assert.True(result.Succeeded);
        Assert.Equal(2, statuses.Count);
        Assert.Equal(ComponentHealth.Installed,
            viewModel.ComponentCards.Single(card => card.Component == ComponentKind.RenoDx).Health);
    }

    [Fact]
    public async Task ExecutableOrFolderOverrideReevaluatesOnlySelectedGame()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("game");
        var original = temp.Pe("game/game.exe");
        var replacement = temp.Pe("game/bin/other.exe");
        var deployment = Path.GetDirectoryName(replacement)!;
        var game = new InstalledGame(new("override-game"), "First", GameStore.Steam, GameLauncher.Steam,
            "10", 10, root, original, null, root, GameBinaryPlatform.Windows, CompatibilityEnvironment.Proton,
            [], null, [], DetectionConfidence.High);
        var discovery = new FakeDiscovery([game]);
        var statuses = new CountingStatusProvider();
        var viewModel = Create(discovery, statusProvider: statuses);
        await viewModel.InitializeAsync();
        var initialScans = discovery.ScanCount;
        var initialDetections = statuses.Count;

        await viewModel.SaveOverridesAsync(replacement, deployment);

        Assert.Equal(initialScans, discovery.ScanCount);
        Assert.Equal(initialDetections + 1, statuses.Count);
        Assert.Equal(replacement, viewModel.SelectedGame?.Executable);
        Assert.False(viewModel.IsSelectionLoading);
    }

    [Fact]
    public async Task OperationSuccessIsRejectedWhenPostDetectionDoesNotConfirmIt()
    {
        var game = Game(10, "First");
        var statuses = new SequenceStatusProvider(ComponentHealth.Available, ComponentHealth.Available);
        var viewModel = new MainViewModel(new FakeDiscovery([game]), statuses, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new SuccessfulExecutor());
        await viewModel.SelectAsync(game);
        var plan = new DeploymentPlan
        {
            Id = "fixture",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory ?? game.GameRoot,
            Action = "install RenoDx",
            ExpectedComponentStates = [new(ComponentKind.RenoDx, true)]
        };

        var result = await viewModel.ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.Equal(UiState.Error, viewModel.CurrentState);
        Assert.Contains("did not confirm", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongAppIdPlanIsRejectedBeforeExecutorRuns()
    {
        var game = Game(10, "First");
        var executor = new RecordingExecutor(new ExecutionResult(true, false, false, []));
        var viewModel = new MainViewModel(new FakeDiscovery([game]), new FakeStatusProvider(), new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: executor);
        await viewModel.SelectAsync(game);
        var wrongGame = Game(20, "Second");
        var plan = Plan(wrongGame, "install ReShade", new ComponentStateExpectation(ComponentKind.ReShade, true));

        var result = await viewModel.ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.Contains("selected game changed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, executor.Count);
        Assert.Equal(10u, viewModel.SelectedGame?.AppId);
    }

    [Fact]
    public async Task PlanPreparedForOldSelectionIsCancelledAndNeverPublishedAsReady()
    {
        var first = Game(10, "First");
        var second = Game(20, "Second");
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<DeploymentPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = Create(new FakeDiscovery([first, second]), recommendedPlanBuilder: (_, _) =>
        {
            requested.TrySetResult();
            return completion.Task;
        });
        await viewModel.SelectAsync(first);

        var planning = viewModel.BuildRecommendedStackPlanAsync();
        await requested.Task;
        await viewModel.SelectAsync(second);
        completion.TrySetResult(Plan(first, "install recommended setup", new ComponentStateExpectation(ComponentKind.ReShade, true)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planning);
        Assert.Equal(20u, viewModel.SelectedGame?.AppId);
        Assert.DoesNotContain("ready for review", viewModel.GlobalStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task FailedAndCancelledModifyingAttemptsBothRescanDiskState()
    {
        var game = Game(10, "First");
        var failedStatuses = new CountingStatusProvider();
        var failed = new MainViewModel(new FakeDiscovery([game]), failedStatuses, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()),
            executor: new RecordingExecutor(new ExecutionResult(false, false, true, [], "rolled back")));
        await failed.SelectAsync(game);

        var failedResult = await failed.ExecuteAsync(Plan(game, "install ReShade", new ComponentStateExpectation(ComponentKind.ReShade, true)), false);

        Assert.False(failedResult.Succeeded);
        Assert.Equal(2, failedStatuses.Count);
        Assert.Equal(UiState.Error, failed.CurrentState);

        var cancelledStatuses = new CountingStatusProvider();
        var cancelled = new MainViewModel(new FakeDiscovery([game]), cancelledStatuses, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new CancellingExecutor());
        await cancelled.SelectAsync(game);

        var cancelledResult = await cancelled.ExecuteAsync(Plan(game, "remove ReShade", new ComponentStateExpectation(ComponentKind.ReShade, false)), false);

        Assert.False(cancelledResult.Succeeded);
        Assert.Equal(2, cancelledStatuses.Count);
        Assert.Equal(UiState.Ready, cancelled.CurrentState);
        Assert.Equal("Operation cancelled", cancelled.GlobalStatus);
        Assert.False(cancelled.HasError);
    }

    [Fact]
    public async Task PostOperationVerificationRequiresEveryExpectedComponentState()
    {
        var game = Game(10, "First");
        var incomplete = new PerScanComponentStatusProvider(
            Statuses(game.AppId, ComponentHealth.Available),
            ComponentStatuses((ComponentKind.ReShade, ComponentHealth.Installed),
                (ComponentKind.RenoDx, ComponentHealth.Installed),
                (ComponentKind.OptiScaler, ComponentHealth.Available)));
        var incompleteViewModel = new MainViewModel(new FakeDiscovery([game]), incomplete, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new SuccessfulExecutor());
        await incompleteViewModel.SelectAsync(game);
        var stackPlan = Plan(game, "install recommended setup",
            new ComponentStateExpectation(ComponentKind.ReShade, true),
            new ComponentStateExpectation(ComponentKind.RenoDx, true),
            new ComponentStateExpectation(ComponentKind.OptiScaler, true));

        var incompleteResult = await incompleteViewModel.ExecuteAsync(stackPlan, false);

        Assert.False(incompleteResult.Succeeded);
        Assert.Contains("every requested component state", incompleteResult.Error, StringComparison.Ordinal);

        var optiOnly = new PerScanComponentStatusProvider(
            Statuses(game.AppId, ComponentHealth.Available),
            ComponentStatuses((ComponentKind.ReShade, ComponentHealth.Available),
                (ComponentKind.RenoDx, ComponentHealth.Unsupported),
                (ComponentKind.OptiScaler, ComponentHealth.Installed)));
        var optiViewModel = new MainViewModel(new FakeDiscovery([game]), optiOnly, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new SuccessfulExecutor());
        await optiViewModel.SelectAsync(game);

        var optiResult = await optiViewModel.ExecuteAsync(
            Plan(game, "install OptiScaler", new ComponentStateExpectation(ComponentKind.OptiScaler, true)), false);

        Assert.True(optiResult.Succeeded);
    }

    [Fact]
    public async Task PrimaryActionUsesInstallUpdateRepairAndRemoveSemantics()
    {
        var cases = new[]
        {
            (ComponentHealth.Available, PrimaryActionKind.Install, "Install"),
            (ComponentHealth.Outdated, PrimaryActionKind.Update, "Update"),
            (ComponentHealth.Broken, PrimaryActionKind.Repair, "Repair"),
            (ComponentHealth.Installed, PrimaryActionKind.Remove, "Remove all managed components")
        };
        foreach (var (health, action, text) in cases)
        {
            var game = Game((uint)(100 + (int)health), health.ToString());
            var reports = new ControlledStackStatusProvider();
            var viewModel = Create(new FakeDiscovery([game]), stackStatusProvider: reports);
            await viewModel.SelectAsync(game);
            await reports.WaitForRequestAsync(game.AppId);
            reports.Complete(game, health, MetadataCheckState.Online, cached: false);
            await WaitUntilAsync(() => !viewModel.IsCheckingUpdates);

            Assert.Equal(action, viewModel.PrimaryAction);
            Assert.Equal(text, viewModel.PrimaryActionText);
            Assert.True(viewModel.CanInstallRecommendedStack);
            Assert.Equal(action != PrimaryActionKind.Remove, viewModel.CanRunPrimarySetup);
        }
    }

    [Fact]
    public async Task PrimaryUpdateAndRemoveBuildPlansWithMatchingConfirmationActions()
    {
        var updateGame = Game(10, "Update");
        var updateReports = new ControlledStackStatusProvider();
        var basePlan = Plan(updateGame, "install recommended setup",
            new ComponentStateExpectation(ComponentKind.ReShade, true));
        var updateViewModel = Create(new FakeDiscovery([updateGame]), stackStatusProvider: updateReports,
            recommendedPlanBuilder: (_, _) => Task.FromResult(basePlan));
        await updateViewModel.SelectAsync(updateGame);
        await updateReports.WaitForRequestAsync(updateGame.AppId);
        updateReports.Complete(updateGame, ComponentHealth.Outdated, MetadataCheckState.Online, cached: false);
        await WaitUntilAsync(() => !updateViewModel.IsCheckingUpdates);

        var updatePlan = await updateViewModel.BuildPrimaryActionPlanAsync();

        Assert.Equal("update installed components", updatePlan.Action);
        Assert.Equal("Update", new DeploymentPlanDialogViewModel(updatePlan).ActionButtonText);

        var removeGame = Game(20, "Remove");
        var removeReports = new ControlledStackStatusProvider();
        var removeViewModel = Create(new FakeDiscovery([removeGame]), stackStatusProvider: removeReports);
        await removeViewModel.SelectAsync(removeGame);
        await removeReports.WaitForRequestAsync(removeGame.AppId);
        removeReports.Complete(removeGame, ComponentHealth.Installed, MetadataCheckState.Online, cached: false);
        await WaitUntilAsync(() => !removeViewModel.IsCheckingUpdates);

        var removePlan = await removeViewModel.BuildPrimaryActionPlanAsync();

        Assert.Equal("remove recommended setup", removePlan.Action);
        Assert.Equal("Remove all managed components", new DeploymentPlanDialogViewModel(removePlan).ActionButtonText);
        Assert.All(removePlan.ExpectedComponentStates, expectation => Assert.False(expectation.Installed));
    }

    [Fact]
    public async Task EligibleOptiScalerRemainsAutomaticWhenRenoAndReShadeCannotBeManaged()
    {
        var game = Game(30, "Opti only");
        var reports = new ControlledStackStatusProvider();
        var optiPlan = Plan(game, "install recommended setup",
            new ComponentStateExpectation(ComponentKind.OptiScaler, true));
        var viewModel = Create(new FakeDiscovery([game]), stackStatusProvider: reports,
            recommendedPlanBuilder: (_, _) => Task.FromResult(optiPlan));
        await viewModel.SelectAsync(game);
        await reports.WaitForRequestAsync(game.AppId);
        reports.Complete(game, ComponentStatuses(
                (ComponentKind.ReShade, ComponentHealth.ForeignInstallation),
                (ComponentKind.RenoDx, ComponentHealth.Unsupported),
                (ComponentKind.OptiScaler, ComponentHealth.DownloadRequired)),
            canAcquireReno: false, canAcquireOpti: true, canInstallRecommendedStack: false);
        await WaitUntilAsync(() => !viewModel.IsCheckingUpdates);

        Assert.Equal(PrimaryActionKind.Install, viewModel.PrimaryAction);
        Assert.True(viewModel.CanInstallRecommendedStack);
        var plan = await viewModel.BuildPrimaryActionPlanAsync();
        Assert.Collection(plan.ExpectedComponentStates,
            expectation => Assert.Equal(new ComponentStateExpectation(ComponentKind.OptiScaler, true), expectation));
    }

    [Fact]
    public void ReducedMotionPreferenceDisablesDecorativeTransitions()
    {
        var viewModel = Create(new FakeDiscovery([]));
        Assert.True(viewModel.UseMotion);
        viewModel.ReduceMotion = true;
        Assert.False(viewModel.UseMotion);
    }

    [Fact]
    public async Task SuccessfulInstallOrRemovalRefreshesCurrentGameState()
    {
        var game = Game(10, "First");
        var statuses = new CountingStatusProvider();
        var viewModel = new MainViewModel(new FakeDiscovery([game]), statuses, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new SuccessfulExecutor());
        await viewModel.SelectAsync(game);
        Assert.Equal(1, statuses.Count);
        var plan = new DeploymentPlan
        {
            Id = "fixture",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory ?? game.GameRoot,
            Action = "remove ReShade",
            ExpectedComponentStates = [new(ComponentKind.ReShade, false)]
        };

        Assert.True((await viewModel.ExecuteAsync(plan, false)).Succeeded);

        Assert.Equal(2, statuses.Count);
        Assert.All(viewModel.ComponentCards, x => Assert.Contains("scan 2", x.TechnicalExplanation, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LateAutomaticUpdateCheckPreservesVerifiedOperationResultAndStatus()
    {
        var game = Game(10, "First");
        var statuses = new PerScanComponentStatusProvider(
            Statuses(game.AppId, ComponentHealth.Available),
            Statuses(game.AppId, ComponentHealth.Installed));
        var reports = new ControlledStackStatusProvider();
        var viewModel = new MainViewModel(new FakeDiscovery([game]), statuses, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new SuccessfulExecutor(),
            stackStatusProvider: reports);
        await viewModel.SelectAsync(game);
        await reports.WaitForRequestAsync(game.AppId);
        reports.Complete(game, ComponentHealth.Available, MetadataCheckState.Online, cached: false);
        await WaitUntilAsync(() => !viewModel.IsCheckingUpdates);

        var result = await viewModel.ExecuteAsync(
            Plan(game, "install ReShade", new ComponentStateExpectation(ComponentKind.ReShade, true)), false);

        Assert.True(result.Succeeded);
        Assert.True(viewModel.IsCheckingUpdates);
        Assert.Equal(UiState.Success, viewModel.CurrentState);
        Assert.Equal("Changes completed", viewModel.GlobalStatus);
        Assert.True(viewModel.PreviousOperationResult?.Succeeded);
        Assert.All(viewModel.ComponentCards, card => Assert.False(card.HasCachePath));

        reports.Complete(game, ComponentHealth.Installed, MetadataCheckState.Online, cached: true);
        await WaitUntilAsync(() => !viewModel.IsCheckingUpdates && viewModel.ComponentCards.All(card => card.HasCachePath));

        Assert.Equal(UpdateCheckState.UpToDate, viewModel.UpdateState);
        Assert.All(viewModel.ComponentCards, card => Assert.True(card.HasCachePath));
        Assert.Equal(UiState.Success, viewModel.CurrentState);
        Assert.Equal("Changes completed", viewModel.GlobalStatus);
        Assert.True(viewModel.PreviousOperationResult?.Succeeded);
    }

    [Fact]
    public void ComponentCardsUseSimpleUserFacingStates()
    {
        Assert.Equal("Installed", Card(ComponentHealth.Installed).State);
        Assert.Equal("Update available", Card(ComponentHealth.Outdated).State);
        Assert.Equal("Unsupported", Card(ComponentHealth.Unsupported).State);
        Assert.Equal("Exact addon available", Card(ComponentHealth.DownloadRequired, Resolved(ComponentKind.RenoDx)).State);
        Assert.Equal("Not installed", Card(ComponentHealth.DownloadRequired,
            Resolved(ComponentKind.RenoDx, ArtifactSupportKind.UnityFallback)).State);
        Assert.Equal("Repair needed", Card(ComponentHealth.Broken).State);
        Assert.Equal("Needs attention", Card(ComponentHealth.RepairAvailable).State);
        Assert.Equal("Repair needed", new ComponentCardViewModel(new ComponentStatus(
            ComponentKind.OptiScaler, ComponentHealth.IncorrectlyConfigured, "1", ["dxgi.dll"], "broken ini",
            InstallationVerification.RepairNeeded)).State);
        Assert.Equal("Experimental", Card(ComponentHealth.Experimental).State);
        Assert.Equal("Blocked", Card(ComponentHealth.Conflicting).State);
        Assert.Equal("Blocked", Card(ComponentHealth.MissingDependency).State);
        Assert.Equal("Unknown existing installation", Card(ComponentHealth.ForeignInstallation).State);
        Assert.Equal("Unknown existing installation", Card(ComponentHealth.ManifestUnavailable).State);

        Assert.All(Enum.GetValues<ComponentHealth>(), health => Assert.Contains(Card(health).State,
            new[]
            {
                "Installed", "Installed, metadata incomplete", "Installed manually", "Not installed", "Update available",
                "Repair needed", "Needs attention", "Experimental", "Unsupported", "Blocked",
                "Unknown existing installation", "Not listed", "Unavailable", "Catalog unavailable",
                "Manual download required", "Addon available for another executable", "Exact addon available"
            }));
    }

    [Fact]
    public void NormalInstallHandlerDoesNotOpenALocalFilePicker()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "RhiLinux.Gui", "Views", "GameDetailsView.axaml.cs"));
        var normalStart = source.IndexOf("ComponentAction_Click", StringComparison.Ordinal);
        var troubleshootingStart = source.IndexOf("UseLocalArtifact_Click", normalStart, StringComparison.Ordinal);
        var normalFlow = source[normalStart..troubleshootingStart];

        Assert.Contains("BuildAutomaticComponentPlanAsync(card.Component)", normalFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildAutomaticPlanAsync();", normalFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFilePickerAsync", normalFlow, StringComparison.Ordinal);
        Assert.Contains("OpenFilePickerAsync", source[troubleshootingStart..], StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentDialogConfirmsOneClickActionAndReportsRollback()
    {
        var gameRoot = Path.Combine(Path.GetTempPath(), "gui-plan-fixture");
        var plan = new DeploymentPlan { Id = "test", InstallId = "steam:10:legacy", SteamAppId = 10, GameRoot = gameRoot, DeploymentDirectory = gameRoot, Action = "remove RenoDX" };
        plan.Operations.Add(new(DeploymentOperationType.DeleteManagedFile, Path.Combine(gameRoot, "managed.dll")));
        var viewModel = new DeploymentPlanDialogViewModel(plan);

        Assert.Equal("Remove", viewModel.ActionButtonText);
        Assert.Equal("Ready to remove", viewModel.Title);
        Assert.Equal("Removal plan", viewModel.PlanSectionTitle);
        Assert.DoesNotContain("dry", viewModel.ExecutionHint, StringComparison.OrdinalIgnoreCase);
        viewModel.Begin();
        Assert.True(viewModel.IsRunning);
        viewModel.Complete(new ExecutionResult(false, false, true, [], "copy failed"));
        Assert.False(viewModel.IsRunning);
        Assert.Equal("Operation failed", viewModel.ResultTitle);
        Assert.True(viewModel.HasRollbackMessage);

        var repairPlan = new DeploymentPlan
        {
            Id = "repair",
            InstallId = "steam:10:legacy",
            SteamAppId = 10,
            GameRoot = gameRoot,
            DeploymentDirectory = gameRoot,
            Action = "install recommended setup",
            RequiresRepair = true,
            CompatibilityMessage = "Some files from an earlier installation need to be repaired before continuing."
        };
        var repairViewModel = new DeploymentPlanDialogViewModel(repairPlan);
        Assert.Equal("Repair", repairViewModel.ActionButtonText);
        Assert.True(repairViewModel.HasCompatibilityMessage);
    }

    [Fact]
    public void NormalGuiContainsNoDryRunTerminology()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        foreach (var file in new[] { "MainWindow.axaml", "DeploymentPlanDialog.axaml", "DeploymentPlanDialog.axaml.cs" })
        {
            var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "RhiLinux.Gui", file));
            Assert.DoesNotContain("dry run", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SavesSearchAndSelectedGamePreferences()
    {
        var preferences = new MemoryPreferencesStore(new UiPreferences());
        var viewModel = Create(new FakeDiscovery([Game(42, "Remembered")]), preferences);
        await viewModel.InitializeAsync();
        viewModel.SearchText = "remember";
        await viewModel.SavePreferencesAsync();

        Assert.Equal("remember", preferences.Saved?.SearchText);
        Assert.Equal(42u, preferences.Saved?.SelectedAppId);
        Assert.False(string.IsNullOrWhiteSpace(preferences.Saved?.SelectedInstallId));
    }

    [Fact]
    public async Task PlanIsRejectedAfterSameGameReselectionGenerationAdvances()
    {
        var game = Game(10, "First");
        var viewModel = Create(new FakeDiscovery([game]));
        await viewModel.InitializeAsync();
        var plan = await viewModel.BuildRestorePlanAsync();
        Assert.True(viewModel.IsPlanForCurrentSelection(plan));

        await viewModel.SelectAsync(game);
        Assert.False(viewModel.IsPlanForCurrentSelection(plan));
    }

    [Fact]
    public async Task SettingsResetRestoresDefaultsAndPersists()
    {
        var preferences = new MemoryPreferencesStore(new UiPreferences
        {
            CacheLimitMiB = 512,
            CheckForUpdatesAutomatically = false,
            AdditionalSteamLibrary = "/tmp/extra",
            ReduceMotion = true
        });
        var viewModel = Create(new FakeDiscovery([]), preferences);
        await viewModel.InitializeAsync();

        viewModel.ResetSettingsToDefaults();

        Assert.Equal(5 * 1024, preferences.Saved?.CacheLimitMiB);
        Assert.True(preferences.Saved?.CheckForUpdatesAutomatically);
        Assert.Equal(string.Empty, preferences.Saved?.AdditionalSteamLibrary);
        Assert.False(preferences.Saved?.ReduceMotion);
    }

    [Fact]
    public async Task SearchRankingPutsExactTitleBeforeWeakSubstring()
    {
        var viewModel = Create(new FakeDiscovery([
            Game(10, "Cyberpunk 2077"),
            Game(20, "Punk Band"),
            Game(30, "Cyberpunk")
        ]));
        await viewModel.InitializeAsync();

        viewModel.SearchText = "Cyberpunk";
        Assert.Equal(30u, viewModel.FilteredGames[0].AppId);
        Assert.Equal(10u, viewModel.FilteredGames[1].AppId);
    }

    [Fact]
    public async Task CancelBackgroundWorkAdvancesSelectionGeneration()
    {
        var viewModel = Create(new FakeDiscovery([Game(10, "First")]));
        await viewModel.InitializeAsync();
        var firstPlan = await viewModel.BuildRestorePlanAsync();
        Assert.True(viewModel.IsPlanForCurrentSelection(firstPlan));

        viewModel.CancelBackgroundWork();
        Assert.False(viewModel.IsPlanForCurrentSelection(firstPlan));
    }

    [Fact]
    public async Task DefaultAndCustomCacheLimitsArePreserved()
    {
        Assert.Equal(5L * 1024 * 1024 * 1024, ArtifactCacheService.DefaultLimitBytes);
        Assert.Equal(5 * 1024, new UiPreferences().CacheLimitMiB);
        var preferences = new MemoryPreferencesStore(new UiPreferences { CacheLimitMiB = 768 });
        var viewModel = Create(new FakeDiscovery([]), preferences);

        await viewModel.InitializeAsync();

        Assert.Equal(768, viewModel.Preferences.CacheLimitMiB);
        Assert.Equal(0.75m, viewModel.CacheLimitGiB);
    }

    [Fact]
    public async Task DisconnectedGameArtifactPinsSurviveDiscoveryRefresh()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("current-game");
        var executable = temp.Pe("current-game/Game.exe");
        var current = InstalledGame.FromSteamGame(new SteamGame(20, "Current", temp.Path, temp.Path, root,
            temp.Combine("compatdata", "20", "pfx"), executable, root, DetectionConfidence.High,
            "fixture", GameEngine.Unknown, []));
        var currentBlob = new string('c', 64);
        var manifestPath = temp.Combine("current-game", ".rhi-linux", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(new GameManifest
        {
            InstallId = current.EffectiveInstallId,
            SteamAppId = current.SteamAppId,
            Files = [new("managed.dll", ComponentKind.OptiScaler, new string('d', 64), "1", null, null,
                SourceBlobSha256: currentBlob)]
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var disconnected = Game(10, "Disconnected");
        var state = new ApplicationState
        {
            DiscoveredGames = [PersistedGameEntry.FromInstalledGame(disconnected)],
            ArtifactReferencesByInstallId = { [disconnected.EffectiveInstallId] = [new string('a', 64)] }
        };
        var store = new MemoryStateStore(state);
        var viewModel = new MainViewModel(new FakeDiscovery([current]), new FakeStatusProvider(), store,
            new MemoryPreferencesStore(new UiPreferences()));

        await viewModel.InitializeAsync();

        Assert.Equal(new string('a', 64), Assert.Single(store.State.ArtifactReferencesByInstallId[disconnected.EffectiveInstallId]));
        Assert.Equal(currentBlob, Assert.Single(store.State.ArtifactReferencesByInstallId[current.EffectiveInstallId]));
    }

    [Fact]
    public void TechnicalComponentDetailsAreCollapsedByDefault()
    {
        var resolved = CachedResolved(ComponentKind.OptiScaler);
        var card = new ComponentCardViewModel(new ComponentStatus(ComponentKind.OptiScaler,
            ComponentHealth.Conflicting, "v1", ["amd_fidelityfx_dx12.dll"], "raw filename conflict"), resolved);

        Assert.False(card.IsDetailsExpanded);
        Assert.DoesNotContain("amd_fidelityfx_dx12.dll", card.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("raw filename conflict", card.TechnicalExplanation, StringComparison.Ordinal);
        Assert.True(card.HasHash || card.HasCachePath);
    }

    [Fact]
    public void NormalComponentCardHasOneVisiblePrimaryActionAndHidesTechnicalFields()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "RhiLinux.Gui", "Views", "GameDetailsView.axaml"));
        var action = source.IndexOf("Content=\"{Binding ActionText}\"", StringComparison.Ordinal);
        var advanced = source.IndexOf("Header=\"Advanced details\"", StringComparison.Ordinal);
        var hash = source.IndexOf("StringFormat='SHA-256: {0}'", StringComparison.Ordinal);
        var cache = source.IndexOf("StringFormat='Cache: {0}'", StringComparison.Ordinal);

        Assert.True(action >= 0 && action < advanced);
        Assert.Equal(action, source.LastIndexOf("Content=\"{Binding ActionText}\"", StringComparison.Ordinal));
        Assert.True(hash > advanced);
        Assert.True(cache > advanced);
    }

    [Fact]
    public void ExecutableAndFilesystemDiagnosticsAreCollapsedByDefault()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "RhiLinux.Gui", "Views", "GameDetailsView.axaml"));
        var advanced = source.IndexOf("Header=\"Advanced game details\"", StringComparison.Ordinal);
        var executable = source.IndexOf("Text=\"{Binding ExecutableDisplay}\"", StringComparison.Ordinal);
        var deployment = source.IndexOf("Text=\"{Binding DeploymentDisplay}\"", StringComparison.Ordinal);
        var prefix = source.IndexOf("Text=\"{Binding ProtonPrefixDisplay}\"", StringComparison.Ordinal);

        Assert.True(advanced >= 0 && advanced < executable);
        Assert.True(executable < deployment && deployment < prefix);
        Assert.DoesNotContain("Header=\"Advanced game details\" IsExpanded=\"True\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Advanced executable details", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Issues\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Readiness.Issues", source, StringComparison.Ordinal);
    }

    private static ComponentCardViewModel Card(ComponentHealth health, ResolvedArtifact? resolved = null) =>
        new(new ComponentStatus(ComponentKind.RenoDx, health, null, [], "fixture state"), resolved);
    private static DeploymentPlan Plan(
        InstalledGame game,
        string action,
        params ComponentStateExpectation[] expectations) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory ?? game.GameRoot,
            Action = action,
            ExpectedComponentStates = [.. expectations]
        };
    private static IReadOnlyList<ComponentStatus> ComponentStatuses(
        params (ComponentKind Component, ComponentHealth Health)[] components) =>
        components.Select(x => new ComponentStatus(x.Component, x.Health,
            x.Health == ComponentHealth.Available ? null : "1", [], $"{x.Component} {x.Health}")).ToArray();
    private static GameReadinessResult ReadinessResult(
        InstalledGame game,
        GameReadinessState state,
        ComponentHealth health) => new(
        game.InstallId,
        state,
        state.ToString(),
        "Review",
        game.ToDeploymentTarget(),
        Statuses(game.AppId, health),
        [],
        null,
        null,
        null,
        DateTimeOffset.UtcNow,
        game.EffectiveInstallId);
    private static ResolvedArtifact Resolved(ComponentKind component, ArtifactSupportKind support = ArtifactSupportKind.ExactGameProfile)
    {
        var selection = new ArtifactSelection(component, "1", new Uri("https://github.com/example/project/releases/download/v1/file.addon64"),
            "v1", "file.addon64", PeArchitecture.X64, null, "file.addon64", null, ArtifactArchiveKind.None);
        return new(component, "1", selection.SourceUrl, "v1", selection.AssetName, PeArchitecture.X64, "fixture",
            support, ArtifactCacheState.DownloadRequired, ArtifactValidationState.NotValidated, null, null, selection);
    }
    private static MainViewModel Create(
        FakeDiscovery discovery,
        MemoryPreferencesStore? preferences = null,
        IComponentStatusProvider? statusProvider = null,
        IStackStatusProvider? stackStatusProvider = null,
        Func<InstalledGame, CancellationToken, Task<DeploymentPlan>>? recommendedPlanBuilder = null,
        IGameReadinessService? readinessService = null) => new(
        discovery,
        statusProvider ?? new FakeStatusProvider(),
        new MemoryStateStore(),
        preferences ?? new MemoryPreferencesStore(new UiPreferences()),
        stackStatusProvider: stackStatusProvider,
        recommendedPlanBuilder: recommendedPlanBuilder,
        readinessService: readinessService);
    private static InstalledGame Game(uint id, string name, GameEngine engine = GameEngine.Unknown) =>
        InstalledGame.FromSteamGame(new SteamGame(
            id, name, "/fixture/steam", "/fixture/library", $"/fixture/game/{id}", $"/fixture/pfx/{id}",
            $"/fixture/game/{id}/game.exe", $"/fixture/game/{id}", DetectionConfidence.High, "fixture", engine, []));

    private sealed class FakeDiscovery(IReadOnlyList<InstalledGame> games) : IGameDiscovery
    {
        public IReadOnlyList<InstalledGame> Games { get; set; } = games;
        public Exception? Error { get; set; }
        public int ScanCount { get; private set; }
        public Task<ScanResult> ScanAsync(IReadOnlyDictionary<string, GameOverride> overrides, CancellationToken cancellationToken)
        {
            ScanCount++;
            return Error is null ? Task.FromResult(new ScanResult(Games, [], [], [])) : Task.FromException<ScanResult>(Error);
        }
    }

    private sealed class ControlledDiscovery : IGameDiscovery
    {
        private readonly TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ScanResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ScanResult> ScanAsync(IReadOnlyDictionary<string, GameOverride> overrides, CancellationToken cancellationToken)
        {
            requested.TrySetResult();
            return completion.Task;
        }
        public Task WaitForRequestAsync() => requested.Task;
        public void Complete(IReadOnlyList<InstalledGame> games) => completion.TrySetResult(new(games, [], [], []));
        public void Cancel() => completion.TrySetCanceled();
    }

    private sealed class FakeStatusProvider : IComponentStatusProvider
    {
        public ComponentHealth ReShadeHealth { get; set; } = ComponentHealth.Available;
        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ComponentStatus>>(
        [
            new(ComponentKind.ReShade, ReShadeHealth, null, [], ReShadeHealth == ComponentHealth.Available ? "fixture" : "fixture changed"),
            new(ComponentKind.RenoDx, ComponentHealth.Available, null, [], "fixture"),
            new(ComponentKind.OptiScaler, ComponentHealth.Available, null, [], "fixture")
        ]);
    }

    private sealed class FixedReadinessService(
        IReadOnlyDictionary<uint, GameReadinessState> states,
        IReadOnlyDictionary<uint, Task<GameReadinessResult>>? pending = null,
        uint? outdatedAppId = null) : IGameReadinessService
    {
        private readonly HashSet<string> evaluatedInstallIds = new(StringComparer.Ordinal);
        private readonly object gate = new();
        public int EvaluationCount { get { lock (gate) return evaluatedInstallIds.Count; } }

        public Task<GameReadinessResult> EvaluateAsync(
            InstalledGame game,
            GameReadinessEvaluationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            lock (gate) evaluatedInstallIds.Add(game.EffectiveInstallId);
            if (pending?.TryGetValue(game.AppId, out var task) == true) return task;
            var state = states[game.AppId];
            var health = game.AppId == outdatedAppId ? ComponentHealth.Outdated : ComponentHealth.Available;
            return Task.FromResult(ReadinessResult(game, state, health));
        }

        public void Invalidate(GameInstallId installId) { }
        public void InvalidateAll() { }
    }

    private sealed class ControlledStatusProvider : IComponentStatusProvider
    {
        private readonly Dictionary<uint, TaskCompletionSource<IReadOnlyList<ComponentStatus>>> pending = [];

        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<IReadOnlyList<ComponentStatus>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[game.AppId] = completion;
            return completion.Task;
        }

        public void Complete(uint appId) => pending[appId].SetResult(new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler }
            .Select(component => new ComponentStatus(component, ComponentHealth.Available, null, [], $"game {appId}"))
            .ToArray());
    }

    private sealed class CountingStatusProvider : IComponentStatusProvider
    {
        public int Count { get; private set; }

        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult<IReadOnlyList<ComponentStatus>>(new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler }
                .Select(component => new ComponentStatus(component, ComponentHealth.Available, null, [], $"scan {Count}"))
                .ToArray());
        }
    }

    private sealed class PerGameStatusProvider(IReadOnlyDictionary<uint, ComponentHealth> health) : IComponentStatusProvider
    {
        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ComponentStatus>>(new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler }
                .Select(component => new ComponentStatus(component, health[game.AppId], "fixture", [component + ".dll"], $"game {game.AppId}"))
                .ToArray());
    }

    private sealed class FailingPerGameStatusProvider(uint failingAppId) : IComponentStatusProvider
    {
        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken) =>
            game.AppId == failingAppId
                ? Task.FromException<IReadOnlyList<ComponentStatus>>(new IOException("fixture failure"))
                : Task.FromResult(Statuses(game.AppId, ComponentHealth.Available));
    }

    private sealed class SequenceStatusProvider(params ComponentHealth[] sequence) : IComponentStatusProvider
    {
        public int Count { get; private set; }
        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken)
        {
            var health = sequence[Math.Min(Count, sequence.Length - 1)];
            Count++;
            return Task.FromResult(Statuses(game.AppId, health));
        }
    }

    private sealed class PerScanComponentStatusProvider(params IReadOnlyList<ComponentStatus>[] scans) : IComponentStatusProvider
    {
        private int count;

        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken)
        {
            var result = scans[Math.Min(count, scans.Length - 1)];
            count++;
            return Task.FromResult(result);
        }
    }

    private sealed class CancellationTrackingStatusProvider : IComponentStatusProvider
    {
        private readonly Dictionary<uint, TaskCompletionSource<IReadOnlyList<ComponentStatus>>> pending = [];
        private readonly Dictionary<uint, TaskCompletionSource> requested = [];
        private readonly Dictionary<uint, TaskCompletionSource> cancelled = [];

        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<IReadOnlyList<ComponentStatus>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[game.AppId] = completion;
            Requested(game.AppId).TrySetResult();
            cancellationToken.Register(() => Cancelled(game.AppId).TrySetResult());
            return completion.Task;
        }

        public Task WaitForRequestAsync(uint appId) => Requested(appId).Task;
        public Task WaitForCancellationAsync(uint appId) => Cancelled(appId).Task;
        public void Complete(uint appId, ComponentHealth health) => pending[appId].TrySetResult(Statuses(appId, health));
        private TaskCompletionSource Requested(uint appId) => Get(requested, appId);
        private TaskCompletionSource Cancelled(uint appId) => Get(cancelled, appId);
        private static TaskCompletionSource Get(Dictionary<uint, TaskCompletionSource> sources, uint appId)
        {
            if (!sources.TryGetValue(appId, out var source)) sources[appId] = source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return source;
        }
    }

    private sealed class JitterStatusProvider : IComponentStatusProvider
    {
        public async Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken)
        {
            await Task.Delay(game.AppId == 10 ? 3 : 1, CancellationToken.None);
            return Statuses(game.AppId, ComponentHealth.Available);
        }
    }

    private sealed class ControlledStackStatusProvider : IStackStatusProvider
    {
        private readonly Dictionary<uint, TaskCompletionSource<StackStatusReport>> pending = [];
        private readonly Dictionary<uint, TaskCompletionSource> requested = [];
        private readonly Dictionary<uint, TaskCompletionSource> cancelled = [];

        public Task<StackStatusReport> GetAsync(InstalledGame game, bool allowNetwork, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            var completion = new TaskCompletionSource<StackStatusReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[game.AppId] = completion;
            Get(requested, game.AppId).TrySetResult();
            cancellationToken.Register(() => Get(cancelled, game.AppId).TrySetResult());
            return completion.Task;
        }

        public Task WaitForRequestAsync(uint appId) => Get(requested, appId).Task;
        public Task WaitForCancellationAsync(uint appId) => Get(cancelled, appId).Task;
        public void Complete(InstalledGame game, ComponentHealth health, MetadataCheckState metadata, bool cached)
        {
            Complete(game, Statuses(game.AppId, health), canAcquireReno: false, canAcquireOpti: true,
                canInstallRecommendedStack: true, metadata: metadata, cached: cached);
        }

        public void Complete(
            InstalledGame game,
            IReadOnlyList<ComponentStatus> statuses,
            bool canAcquireReno,
            bool canAcquireOpti,
            bool canInstallRecommendedStack,
            MetadataCheckState metadata = MetadataCheckState.Online,
            bool cached = false)
        {
            var profile = new GameProfile("fixture", game.AppId, game.Name, [], null, null, game.Engine, "DirectX",
                ["dxgi.dll"], [], [], null, GameProfileSupport.Unsupported, true,
                new Dictionary<string, string>(), [], [], null);
            var match = new GameProfileMatch(profile, "fixture", true);
            var proxy = new ProxySelectionResult("dxgi.dll", DetectionConfidence.High, "fixture", [], [],
                DeploymentPlanner.GenerateLaunchOption("dxgi.dll"));
            var artifacts = new GameArtifactResolution(match, [], [], canAcquireReno || canAcquireOpti, metadata)
            {
                Components = cached ? statuses.Select(status => CachedResolved(status.Component)).ToArray() : [],
                CanAcquireRenoSetup = canAcquireReno,
                CanAcquireOptiScaler = canAcquireOpti
            };
            var eligibility = new OptiScalerEligibility(OptiScalerCompatibilityLevel.KnownCompatible, true, false, "fixture");
            pending[game.AppId].TrySetResult(new(match, proxy, artifacts, statuses, eligibility,
                canInstallRecommendedStack, "fixture"));
        }

        private static TaskCompletionSource Get(Dictionary<uint, TaskCompletionSource> sources, uint appId)
        {
            if (!sources.TryGetValue(appId, out var source)) sources[appId] = source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return source;
        }
    }

    private sealed class ControlledExecutor : IDeploymentExecutor
    {
        private readonly TaskCompletionSource<ExecutionResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ExecutionResult> ExecuteAsync(DeploymentPlan plan, bool dryRun, CancellationToken cancellationToken = default) => completion.Task;
        public void Complete() => completion.TrySetResult(new(true, false, false, []));
    }

    private static IReadOnlyList<ComponentStatus> Statuses(uint appId, ComponentHealth health) =>
        new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler }
            .Select(component => new ComponentStatus(component, health, health == ComponentHealth.Available ? null : "1", [], $"game {appId}"))
            .ToArray();

    private static ResolvedArtifact CachedResolved(ComponentKind component)
    {
        var resolved = Resolved(component);
        return resolved with
        {
            CacheState = ArtifactCacheState.Cached,
            Selection = resolved.Selection! with { CachedPath = $"/fixture/cache/{component}.dll" }
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(5);
        Assert.True(condition());
    }

    private sealed class SuccessfulExecutor : IDeploymentExecutor
    {
        public Task<ExecutionResult> ExecuteAsync(DeploymentPlan plan, bool dryRun, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionResult(true, dryRun, false, []));
    }

    private sealed class RecordingExecutor(ExecutionResult result) : IDeploymentExecutor
    {
        public int Count { get; private set; }
        public Task<ExecutionResult> ExecuteAsync(DeploymentPlan plan, bool dryRun, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.FromResult(result with { DryRun = dryRun });
        }
    }

    private sealed class CancellingExecutor : IDeploymentExecutor
    {
        public Task<ExecutionResult> ExecuteAsync(DeploymentPlan plan, bool dryRun, CancellationToken cancellationToken = default) =>
            Task.FromException<ExecutionResult>(new OperationCanceledException(cancellationToken));
    }

    private sealed class MemoryStateStore(ApplicationState? initial = null) : IStateStore
    {
        private ApplicationState state = initial ?? new();
        public ApplicationState State => state;
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default) { this.state = state; return Task.CompletedTask; }
    }

    private sealed class MemoryPreferencesStore(UiPreferences preferences) : IUiPreferencesStore
    {
        public UiPreferences? Saved { get; private set; }
        public Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(preferences);
        public Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
        {
            Saved = ClonePreferences(preferences);
            return Task.CompletedTask;
        }

        private static UiPreferences ClonePreferences(UiPreferences preferences) => new()
        {
            SchemaVersion = preferences.SchemaVersion,
            WindowWidth = preferences.WindowWidth,
            WindowHeight = preferences.WindowHeight,
            SidebarWidth = preferences.SidebarWidth,
            SelectedAppId = preferences.SelectedAppId,
            SelectedInstallId = preferences.SelectedInstallId,
            Theme = preferences.Theme,
            SearchText = preferences.SearchText,
            CacheLimitMiB = preferences.CacheLimitMiB,
            ReduceMotion = preferences.ReduceMotion,
            CheckForUpdatesAutomatically = preferences.CheckForUpdatesAutomatically,
            AdditionalSteamLibrary = preferences.AdditionalSteamLibrary,
            LibraryFilter = preferences.LibraryFilter,
            WatchSteamLibraries = preferences.WatchSteamLibraries,
            ScanAllSources = preferences.ScanAllSources,
            EnableHeroic = preferences.EnableHeroic,
            EnableLegendary = preferences.EnableLegendary,
            EnableLutris = preferences.EnableLutris,
            EnableBottles = preferences.EnableBottles,
            EnableMinigalaxy = preferences.EnableMinigalaxy,
            AutomaticallyEvaluateReadiness = preferences.AutomaticallyEvaluateReadiness,
            ShowUnsupportedNativeGames = preferences.ShowUnsupportedNativeGames,
            WarnBeforeAntiCheatDeployments = preferences.WarnBeforeAntiCheatDeployments,
            PreferExistingManagedVersions = preferences.PreferExistingManagedVersions,
            RefreshArtifactMetadataOnStartup = preferences.RefreshArtifactMetadataOnStartup
        };
    }
}
