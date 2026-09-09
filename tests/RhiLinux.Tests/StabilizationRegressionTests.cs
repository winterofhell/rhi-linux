using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class StabilizationRegressionTests
{
    [Fact]
    public void GameSearchRanksExactAppIdAboveTitleSubstring()
    {
        var games = new[]
        {
            Game(1091500, "Cyberpunk 2077", GameEngine.Unknown, "Cyberpunk2077.exe"),
            Game(10, "Counter-Strike", GameEngine.Unknown, "hl.exe"),
            Game(100, "Random Ten", GameEngine.Unreal, "Game.exe")
        };

        var ranked = GameSearch.Rank(games, "10");
        Assert.Equal(10u, ranked[0].AppId);
        Assert.Contains(ranked, game => game.AppId == 100);
    }

    [Fact]
    public void GameSearchSupportsTokenEngineAndExecutableQueries()
    {
        var games = new[]
        {
            Game(1, "Night Swarm", GameEngine.Unity, "NightSwarm.exe"),
            Game(2, "Space Quest", GameEngine.Unreal, "SpaceQuest-Win64-Shipping.exe"),
            Game(3, "Helper Tools", GameEngine.Unknown, "setup.exe")
        };

        Assert.Equal(2u, Assert.Single(GameSearch.Rank(games, "quest space")).AppId);
        Assert.Equal(2u, Assert.Single(GameSearch.Rank(games, "Unreal")).AppId);
        Assert.Equal(1u, Assert.Single(GameSearch.Rank(games, "NightSwarm")).AppId);
        Assert.Empty(GameSearch.Rank(games, "zzzznomatch"));
    }

    [Fact]
    public void LaunchOptionObservationDistinguishesStatuses()
    {
        var game = Game(42, "Fixture", GameEngine.Unknown, "Game.exe");
        var required = DeploymentPlanner.GenerateLaunchOption("dxgi.dll");

        Assert.Equal(LaunchOptionStatus.NotRequired, SteamLaunchOptionService.Observe(game, null).Status);
        Assert.Equal(LaunchOptionStatus.NotDetected, SteamLaunchOptionService.Observe(game, required).Status);
        Assert.Equal(LaunchOptionStatus.Missing,
            SteamLaunchOptionService.Observe(game, required, "PROTON_USE_WINED3D=1 %command%").Status);
        Assert.Equal(LaunchOptionStatus.Correct, SteamLaunchOptionService.Observe(game, required, required).Status);
        Assert.Equal(LaunchOptionStatus.NeedsUpdate,
            SteamLaunchOptionService.Observe(game, required, """WINEDLLOVERRIDES="version=n,b" %command%""").Status);
    }

    [Fact]
    public void MergeManagedOverridePreservesUnrelatedArguments()
    {
        var required = DeploymentPlanner.GenerateLaunchOption("dxgi.dll");
        var merged = SteamLaunchOptionService.MergeManagedOverride(
            "PROTON_ENABLE_NVAPI=1 WINEDLLOVERRIDES=\"version=n,b\" %command%", required);
        Assert.Contains("PROTON_ENABLE_NVAPI=1", merged, StringComparison.Ordinal);
        Assert.Contains("dxgi=n,b", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("version=n,b", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationContextRejectsDifferentGeneration()
    {
        var game = Game(10, "Fixture", GameEngine.Unknown, "Game.exe");
        var context = OperationContext.From(game, 3);
        Assert.True(context.Matches(game, 3));
        Assert.False(context.Matches(game, 4));
    }

    [Fact]
    public async Task RemovePlanRecomputesLaunchOptionWhenReShadeRemains()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        var game = InstalledGame.FromSteamGame(new SteamGame(42, "Fixture", temp.Path, temp.Path, root, temp.Combine("pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]));
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "1"),
            null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);

        var remove = await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler);
        Assert.Contains("dxgi=n,b", remove.LaunchOption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MutableIniHashDriftDoesNotMarkRecommendedPlanAsRepair()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        var game = InstalledGame.FromSteamGame(new SteamGame(42, "Fixture", temp.Path, temp.Path, root, temp.Combine("pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]));
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            null,
            null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        await File.AppendAllTextAsync(Path.Combine(game.DeploymentDirectory ?? game.GameRoot, "OptiScaler.ini"), "UserSetting=1\n");

        var plan = await planner.BuildRecommendedStackPlanAsync(game, artifacts);
        Assert.False(plan.RequiresRepair);
    }

    [Fact]
    public void CloseHandlerDoesNotUseSyncOverAsyncGetResult()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = awaitable(Path.Combine(directory!.FullName, "src", "RhiLinux.Gui", "MainWindow.axaml.cs"));
        Assert.DoesNotContain("GetAwaiter().GetResult()", source, StringComparison.Ordinal);
        Assert.Contains("closeRequested", source, StringComparison.Ordinal);
        Assert.Contains("CancelBackgroundWork", source, StringComparison.Ordinal);
        Assert.Contains("eventArgs.Cancel = true", source, StringComparison.Ordinal);

        static string awaitable(string path) => File.ReadAllText(path);
    }

    [Fact]
    public void ThemeStylesProvideSecondaryButtonSurfaces()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var styles = File.ReadAllText(Path.Combine(directory!.FullName, "src", "RhiLinux.Gui", "Styles.axaml"));
        Assert.Contains("Button.secondary", styles, StringComparison.Ordinal);
        Assert.Contains("SurfaceAltBrush", styles, StringComparison.Ordinal);
        Assert.Contains("PrimaryTextBrush", styles, StringComparison.Ordinal);
        Assert.Contains("ControlBrush", styles, StringComparison.Ordinal);
        Assert.Contains("ExpanderHeaderBackground", styles, StringComparison.Ordinal);
        Assert.Contains("ToggleButton#ExpanderHeader", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Color=\"#000000\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Color=\"Black\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Padding\" Value=\"4,0,14,0\"", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowReservesSidebarScrollbarGutterAndUsesDockedHeaderActions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var guiDirectory = Path.Combine(directory!.FullName, "src", "RhiLinux.Gui");
        var source = string.Concat(
            File.ReadAllText(Path.Combine(guiDirectory, "MainWindow.axaml")),
            File.ReadAllText(Path.Combine(guiDirectory, "Views", "LibraryView.axaml")),
            File.ReadAllText(Path.Combine(guiDirectory, "Views", "GameDetailsView.axaml")));
        Assert.Contains("DockPanel", source, StringComparison.Ordinal);
        Assert.Contains("Classes=\"gameList\"", source, StringComparison.Ordinal);
        Assert.Contains("Classes=\"card component\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#000000\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Background=\"Black\"", source, StringComparison.OrdinalIgnoreCase);
    }

    private static InstalledGame Game(uint appId, string name, GameEngine engine, string executable) =>
        InstalledGame.FromSteamGame(new SteamGame(appId, name, "/tmp/steam", "/tmp/steam", $"/tmp/steam/steamapps/common/{name}",
            $"/tmp/steam/steamapps/compatdata/{appId}/pfx", $"/tmp/steam/steamapps/common/{name}/{executable}",
            $"/tmp/steam/steamapps/common/{name}", DetectionConfidence.High,
            "Selected because it is the primary 64-bit game executable.",
            engine, [new($"/tmp/steam/steamapps/common/{name}/{executable}", 100, DetectionConfidence.High,
                PeArchitecture.X64, 1024, ["64-bit PE executable"])]));
}
