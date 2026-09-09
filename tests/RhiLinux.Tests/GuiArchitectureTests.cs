using RhiLinux.Core;
using RhiLinux.Gui;

namespace RhiLinux.Tests;

public sealed class GuiArchitectureTests
{
    [Fact]
    public void ProjectReferencesAndUiSourceRespectApplicationBoundaries()
    {
        var root = FindRepositoryRoot();
        var core = File.ReadAllText(Path.Combine(root, "src", "RhiLinux.Core", "RhiLinux.Core.csproj"));
        var sources = File.ReadAllText(Path.Combine(root, "src", "RhiLinux.Sources", "RhiLinux.Sources.csproj"));
        var mods = File.ReadAllText(Path.Combine(root, "src", "RhiLinux.Mods", "RhiLinux.Mods.csproj"));

        Assert.DoesNotContain("RhiLinux.Gui", core, StringComparison.Ordinal);
        Assert.DoesNotContain("RhiLinux.Gui", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("RhiLinux.Gui", mods, StringComparison.Ordinal);

        var gui = Path.Combine(root, "src", "RhiLinux.Gui");
        var viewModels = Directory.GetFiles(gui, "*ViewModel*.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(gui, "MainViewModel.cs"));
        foreach (var path in viewModels.Distinct(StringComparer.Ordinal))
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("new FileSystemWatcher", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new SqliteConnection", source, StringComparison.Ordinal);
        }

        foreach (var path in Directory.GetFiles(Path.Combine(gui, "Views"), "*", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("new DeploymentPlanner", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new DeploymentExecutor", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new GameOperationCoordinator", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShellXamlHostsFocusedMajorViews()
    {
        var root = FindRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root, "src", "RhiLinux.Gui", "MainWindow.axaml"));

        foreach (var view in new[]
                 {
                     "OverviewView", "LibraryView", "UpdatesView", "DiagnosticsView", "SettingsView", "GameDetailsView"
                 })
            Assert.Contains($"views:{view}", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Overview\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Diagnostics\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Settings\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("ComponentAction_Click", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiDoesNotExposeBulkDeploymentActions()
    {
        var gui = Path.Combine(FindRepositoryRoot(), "src", "RhiLinux.Gui");
        var source = string.Join('\n', Directory.GetFiles(gui, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) ||
                           path.EndsWith(".axaml", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        Assert.DoesNotContain("Install all", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Select all", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BatchReview", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BatchOperation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GameDetailsKeepInstallActionsInsideRequiredFileCards()
    {
        var root = FindRepositoryRoot();
        var details = File.ReadAllText(Path.Combine(root, "src", "RhiLinux.Gui", "Views", "GameDetailsView.axaml"));
        var settings = File.ReadAllText(Path.Combine(root, "src", "RhiLinux.Gui", "Views", "SettingsView.axaml"));

        Assert.DoesNotContain("Content=\"{Binding PrimaryActionText}\"", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"PrimaryAction_Click\"", details, StringComparison.Ordinal);
        Assert.Contains("Click=\"ComponentAction_Click\"", details, StringComparison.Ordinal);
        Assert.Contains("Click=\"UseLocalArtifact_Click\"", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Game Readiness Center", details, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveProfile_Click", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Launcher\"", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Architecture\"", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Engine\"", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore previous state", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restore backups", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delete backup", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Backup storage", settings, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SmoothScrollingUsesDisplaySynchronizedFrames()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "RhiLinux.Gui", "SmoothScrolling.cs"));

        Assert.Contains("RequestAnimationFrame", source, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetElapsedTime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromMilliseconds(8)", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

public sealed class GuiPageViewModelTests
{
    [Fact]
    public void LibraryPageOwnsSearchSourceAndReadinessFiltering()
    {
        var ready = Game("ready", "Ready Heroic", GameLauncher.Heroic, GameStore.Epic);
        var attention = Game("attention", "Attention Lutris", GameLauncher.Lutris, GameStore.Gog);
        var page = new LibraryViewModel();
        var readiness = new Dictionary<string, GameReadinessState>
        {
            [ready.EffectiveInstallId] = GameReadinessState.Ready,
            [attention.EffectiveInstallId] = GameReadinessState.NeedsConfiguration
        };

        page.Apply([ready, attention], "Ready", string.Empty, true, readiness);
        Assert.Equal(ready.InstallId, Assert.Single(page.Games).InstallId);

        page.Apply([ready, attention], "All", "lutris", true, readiness);
        Assert.Equal(attention.InstallId, Assert.Single(page.Games).InstallId);
    }

    [Fact]
    public void DiagnosticsPageProjectsStructuredRefreshMetrics()
    {
        var page = new DiagnosticsViewModel();
        var metrics = LibraryMetrics.Empty with
        {
            Reason = "Heroic metadata changed",
            ProvidersExecuted = ["heroic"],
            SourceDocumentsParsed = 1,
            InstallationsReconciled = 1,
            TotalDuration = TimeSpan.FromMilliseconds(18)
        };

        page.Update(
            [new("heroic", "fixture", SourceDiagnosticSeverity.Warning, "Fixture warning")],
            [new("heroic", "Heroic", true, true, 1, 1, "1 record", "/fixture")],
            metrics);

        Assert.Single(page.Items);
        Assert.Single(page.Providers);
        Assert.Contains("Heroic metadata changed", page.LastRefresh, StringComparison.Ordinal);
        Assert.Contains("Parsed: 1", page.LastRefresh, StringComparison.Ordinal);
        Assert.Contains("18 ms", page.LastRefresh, StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewAndSettingsPagesOwnTheirSummaries()
    {
        var game = Game("game", "Fixture", GameLauncher.Heroic, GameStore.Epic);
        var overview = new OverviewViewModel();
        overview.Update(
            [game],
            new Dictionary<string, GameReadinessState> { [game.EffectiveInstallId] = GameReadinessState.Ready },
            2,
            1);
        var settings = new SettingsViewModel();
        settings.Update(new UiPreferences
        {
            ScanAllSources = true,
            EnableHeroic = true,
            EnableLegendary = false,
            EnableLutris = true,
            EnableBottles = false,
            EnableMinigalaxy = true,
            CacheLimitMiB = 2048
        });

        Assert.Equal("1 games · 1 ready · 0 need attention", overview.Summary);
        Assert.Equal(2, overview.UpdateCount);
        Assert.Equal(5, settings.EnabledSourceCount);
        Assert.Equal(2, settings.CacheLimitGiB);
    }

    private static InstalledGame Game(
        string id,
        string name,
        GameLauncher launcher,
        GameStore store) => new(
        new(id), name, store, launcher, id, null, $"/games/{id}", $"/games/{id}/game.exe",
        null, $"/games/{id}", GameBinaryPlatform.Windows, CompatibilityEnvironment.Wine,
        [], null, [], DetectionConfidence.High);
}
