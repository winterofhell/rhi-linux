using RhiLinux.Core;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class SteamDiscoveryTests
{
    [Fact]
    public async Task InstalledGameWithoutCompatDataIsIncluded()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddGame(temp, "steam", 42, "Fresh Proton Game", "Fresh", "Fresh.exe");

        var game = Assert.Single((await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam])).Games);

        Assert.False(game.HasProtonPrefix);
        Assert.Equal(SteamInstallState.Installed, game.InstallState);
    }

    [Fact]
    public async Task InstalledGameWithoutWindowsExecutableIsIncludedAndDiagnosed()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddManifest(temp, "steam", 42, "No Exe Yet", "NoExe", "4");
        temp.Directory("steam", "steamapps", "common", "NoExe");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam]);

        var game = Assert.Single(result.Games);
        Assert.Null(game.Executable);
        Assert.Equal(DetectionConfidence.None, game.Confidence);
        Assert.Contains("no Windows executable", game.SelectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no Windows executable", Assert.Single(result.ManifestDiagnostics!).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownEngineDoesNotHideInstalledGame()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddGame(temp, "steam", 42, "Unknown Engine", "Unknown", "Unknown.exe");

        var game = Assert.Single((await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam])).Games);

        Assert.Equal(GameEngine.Unknown, game.Engine);
    }

    [Fact]
    public async Task NativeLinuxGameIsIncludedAsUnsupported()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddManifest(temp, "steam", 42, "Native Game", "Native", "4");
        temp.File("steam/steamapps/common/Native/native-game", "\u007fELFnative fixture");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam]);

        var game = Assert.Single(result.Games);
        Assert.True(game.IsNativeLinux);
        Assert.Null(game.Executable);
        Assert.Contains("Native Linux", game.SelectionReason, StringComparison.Ordinal);
        Assert.Equal("Included", Assert.Single(result.ManifestDiagnostics!).Disposition);
    }

    [Fact]
    public async Task IncompleteDownloadBecomesInstalledOnLaterScan()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddManifest(temp, "steam", 42, "Downloading Game", "Downloading", "2");
        var service = new SteamDiscoveryService(new ExecutableDetector());

        var installing = await service.ScanAsync([steam]);
        Assert.Equal(SteamInstallState.Installing, Assert.Single(installing.Games).InstallState);
        Assert.Equal("Installing", Assert.Single(installing.ManifestDiagnostics!).Disposition);

        AddGame(temp, "steam", 42, "Downloading Game", "Downloading", "Downloading.exe", stateFlags: "4");
        var installed = await service.ScanAsync([steam]);
        Assert.Equal(SteamInstallState.Installed, Assert.Single(installed.Games).InstallState);
        Assert.NotNull(Assert.Single(installed.Games).Executable);
    }

    [Fact]
    public async Task SymlinkedSteamRootIsCanonicalizedAndScanned()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("real-steam");
        AddGame(temp, "real-steam", 42, "Linked Game", "Linked", "Linked.exe");
        var link = temp.Combine("steam-link");
        Directory.CreateSymbolicLink(link, steam);

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([link, steam]);

        Assert.Single(result.SteamRoots);
        Assert.Single(result.Games);
        Assert.Equal(Path.GetFullPath(steam), Assert.Single(result.SteamRoots));
    }

    [Fact]
    public async Task NewlyCreatedManifestAppearsOnNextScan()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        var service = new SteamDiscoveryService(new ExecutableDetector());
        Assert.Empty((await service.ScanAsync([steam])).Games);

        AddGame(temp, "steam", 42, "New Game", "NewGame", "NewGame.exe");

        Assert.Equal(42u, Assert.Single((await service.ScanAsync([steam])).Games).AppId);
    }

    [Fact]
    public async Task ChangedLibraryFoldersFileIsReadOnEveryScan()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        var second = temp.Directory("second");
        temp.Directory("steam", "steamapps");
        temp.File("steam/steamapps/libraryfolders.vdf", $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} }}");
        var service = new SteamDiscoveryService(new ExecutableDetector());
        Assert.Empty((await service.ScanAsync([steam])).Games);
        AddGame(temp, "second", 84, "Second Game", "SecondGame", "SecondGame.exe");

        temp.File("steam/steamapps/libraryfolders.vdf", $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} \"1\" {{ \"path\" \"{Escape(second)}\" }} }}");

        Assert.Equal(84u, Assert.Single((await service.ScanAsync([steam])).Games).AppId);
    }

    [Fact]
    public async Task MovedGameUsesCurrentLibraryAndInstallDirectory()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        var second = temp.Directory("second");
        temp.File("steam/steamapps/libraryfolders.vdf", $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} \"1\" {{ \"path\" \"{Escape(second)}\" }} }}");
        AddGame(temp, "steam", 42, "Moving Game", "OldPlace", "Old.exe");
        var service = new SteamDiscoveryService(new ExecutableDetector());
        Assert.Contains("OldPlace", Assert.Single((await service.ScanAsync([steam])).Games).GameRoot, StringComparison.Ordinal);

        File.Delete(temp.Combine("steam", "steamapps", "appmanifest_42.acf"));
        AddGame(temp, "second", 42, "Moving Game", "NewPlace", "New.exe");

        var moved = Assert.Single((await service.ScanAsync([steam])).Games);
        Assert.Equal(Path.GetFullPath(second), moved.LibraryRoot);
        Assert.Contains("NewPlace", moved.GameRoot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedManifestRemovesGameOnNextScan()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddGame(temp, "steam", 42, "Removed Game", "Removed", "Removed.exe");
        var service = new SteamDiscoveryService(new ExecutableDetector());
        Assert.Single((await service.ScanAsync([steam])).Games);

        File.Delete(temp.Combine("steam", "steamapps", "appmanifest_42.acf"));

        Assert.Empty((await service.ScanAsync([steam])).Games);
    }

    [Fact]
    public async Task IncompleteManifestIsRetriedAndDetectedWhenValid()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam", "steamapps");
        temp.File("steam/steamapps/appmanifest_42.acf", "\"AppState\" { \"appid\" \"42\"");
        var service = new SteamDiscoveryService(new ExecutableDetector());
        Assert.Empty((await service.ScanAsync([Path.GetDirectoryName(steam)!])).Games);

        AddGame(temp, "steam", 42, "Completed Game", "Completed", "Completed.exe");

        Assert.Equal(42u, Assert.Single((await service.ScanAsync([Path.GetDirectoryName(steam)!])).Games).AppId);
    }

    [Fact]
    public async Task FindsGamesAcrossLibrariesAndDeduplicatesAppIds()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("native"); var second = temp.Directory("secondary");
        temp.File("native/steamapps/libraryfolders.vdf", $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} \"1\" {{ \"path\" \"{Escape(second)}\" }} }}");
        AddGame(temp, "native", 42, "Example Game", "Example", "Example.exe");
        AddGame(temp, "secondary", 84, "Other Game", "Other", "Other-Win64-Shipping.exe", unreal: true);
        AddGame(temp, "secondary", 42, "Duplicate", "Duplicate", "Duplicate.exe");
        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam]);
        Assert.Equal(2, result.Games.Count);
        Assert.Contains(result.Games, x => x.AppId == 84 && x.ProtonPrefix.EndsWith("compatdata/84/pfx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkipsMalformedManifestWithWarning()
    {
        using var temp = new TestDirectory(); var steam = temp.Directory("steam", "steamapps");
        temp.File("steam/steamapps/appmanifest_bad.acf", "\"AppState\" {");
        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([System.IO.Path.GetDirectoryName(steam)!]);
        Assert.Empty(result.Games); Assert.Single(result.Warnings);
        var diagnostic = Assert.Single(result.ManifestDiagnostics!);
        Assert.Equal("Malformed", diagnostic.Disposition);
        Assert.Contains("appmanifest_bad.acf", diagnostic.ManifestPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HonorsManualExecutableAndDeploymentOverrides()
    {
        using var temp = new TestDirectory(); var steam = temp.Directory("steam");
        AddGame(temp, "steam", 42, "Example", "Example", "launcher.exe");
        var manual = temp.Pe("steam/steamapps/common/Example/bin/real.exe");
        var overrides = new Dictionary<uint, GameOverride> { [42] = new("bin/real.exe", "bin") };
        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam], overrides);
        Assert.Equal(manual, result.Games[0].Executable); Assert.EndsWith("/bin", result.Games[0].DeploymentDirectory);
    }

    [Fact]
    public async Task ExcludesProtonCompatibilityTools()
    {
        using var temp = new TestDirectory(); var steam = temp.Directory("steam");
        AddGame(temp, "steam", 1493710, "Proton Experimental", "Proton - Experimental", "winedbg.exe");
        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam]);
        Assert.Empty(result.Games);
    }

    [Fact]
    public async Task RejectsManifestInstallDirectoryOutsideSteamCommon()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam", "steamapps");
        temp.File("steam/steamapps/appmanifest_42.acf",
            "\"AppState\" { \"appid\" \"42\" \"name\" \"Escaped Game\" \"installdir\" \"../../outside\" }");
        _ = temp.Pe("steam/outside/EscapedGame.exe");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([Path.GetDirectoryName(steam)!]);

        Assert.Empty(result.Games);
        Assert.Contains(result.Warnings, warning => warning.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EveryEnumeratedManifestHasDiagnosticDisposition()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddGame(temp, "steam", 42, "Included", "Included", "Included.exe");
        AddManifest(temp, "steam", 84, "Missing", "Missing", "4");
        temp.File("steam/steamapps/appmanifest_bad.acf", "\"AppState\" {");
        AddGame(temp, "steam", 1493710, "Proton Experimental", "Proton - Experimental", "winedbg.exe");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam]);

        Assert.Equal(4, result.ManifestDiagnostics!.Count);
        Assert.All(result.ManifestDiagnostics, diagnostic =>
        {
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Disposition));
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Reason));
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.ManifestPath));
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.LibraryRoot));
        });
    }

    private static void AddGame(TestDirectory temp, string root, uint id, string name, string install, string exe,
        bool unreal = false, string? stateFlags = null)
    {
        AddManifest(temp, root, id, name, install, stateFlags);
        var relative = unreal ? $"{root}/steamapps/common/{install}/{install}/Binaries/Win64/{exe}" : $"{root}/steamapps/common/{install}/{exe}";
        temp.Pe(relative, size: unreal ? 100 * 1024 * 1024 : 8 * 1024 * 1024);
    }

    private static void AddManifest(TestDirectory temp, string root, uint id, string name, string install,
        string? stateFlags)
    {
        temp.Directory(root, "steamapps");
        var flags = stateFlags is null ? string.Empty : $" \"StateFlags\" \"{stateFlags}\"";
        temp.File($"{root}/steamapps/appmanifest_{id}.acf",
            $"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"{name}\" \"installdir\" \"{install}\"{flags} }}");
    }
    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
