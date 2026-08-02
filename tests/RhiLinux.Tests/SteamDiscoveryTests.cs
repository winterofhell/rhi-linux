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
    public async Task ExplicitRootHasHighestPriorityAndKeepsWorkingNativeResult()
    {
        using var temp = new TestDirectory();
        var native = temp.Directory("native");
        var explicitRoot = temp.Directory("explicit");
        AddGame(temp, "native", 42, "Native Game", "Native", "Native.exe");
        AddGame(temp, "explicit", 84, "Explicit Game", "Explicit", "Explicit.exe");
        temp.Directory("broken", "not-steam");
        var broken = temp.Combine("broken");

        var result = await new SteamDiscoveryService(new ExecutableDetector())
            .ScanAsync([explicitRoot, broken, native], includeDefaultRoots: false);

        Assert.Equal(2, result.Games.Count);
        Assert.Contains(result.Games, game => game.AppId == 42);
        Assert.Contains(result.Games, game => game.AppId == 84);
        Assert.Contains(result.RootDiagnostics!, diagnostic =>
            diagnostic.Source == SteamRootSource.Explicit &&
            diagnostic.CanonicalPath == Path.GetFullPath(explicitRoot));
    }

    [Fact]
    public async Task FlatpakStyleRootIsScannedThroughLibraryFolders()
    {
        using var temp = new TestDirectory();
        var flatpak = temp.Directory("flatpak-home", ".var", "app", "com.valvesoftware.Steam", "data", "Steam");
        var external = temp.Directory("external disk", "SteamLibrary");
        temp.Directory(flatpak, "steamapps");
        temp.File(Path.Combine(Path.GetRelativePath(temp.Path, flatpak), "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(flatpak)}\" }} \"1\" {{ \"path\" \"{Escape(external)}\" }} }}");
        AddGame(temp, Path.GetRelativePath(temp.Path, external), 100, "Flatpak External", "FlatpakExt", "Game.exe");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([flatpak]);

        Assert.Equal(100u, Assert.Single(result.Games).AppId);
        Assert.Contains(result.Libraries, library => library == Path.GetFullPath(external));
    }

    [Fact]
    public async Task SnapStyleRootDiscoversInstalledGame()
    {
        using var temp = new TestDirectory();
        var snap = temp.Directory("snap", "steam", "common", ".local", "share", "Steam");
        AddGame(temp, Path.GetRelativePath(temp.Path, snap), 55, "Snap Game", "SnapGame", "Snap.exe");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([snap]);

        Assert.Equal(55u, Assert.Single(result.Games).AppId);
        Assert.Contains(result.RootDiagnostics!, diagnostic =>
            diagnostic.CanonicalPath == Path.GetFullPath(snap) && diagnostic.GamesIncluded == 1);
    }

    [Fact]
    public async Task XdgDataHomeOverrideIsEnumeratedAsCandidate()
    {
        using var temp = new TestDirectory();
        var xdg = temp.Directory("xdg-data");
        var steam = temp.Directory("xdg-data", "Steam");
        AddGame(temp, Path.GetRelativePath(temp.Path, steam), 77, "Xdg Game", "XdgGame", "Xdg.exe");
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
            var candidates = SteamDiscoveryService.DiscoverRootCandidates(includeDefaultRoots: true);
            Assert.Contains(candidates, candidate =>
                candidate.Source == SteamRootSource.Xdg &&
                candidate.CanonicalPath == Path.GetFullPath(steam) &&
                candidate.Exists);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }

        Assert.Equal(77u, Assert.Single(
            (await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam])).Games).AppId);
    }

    [Fact]
    public async Task DuplicateNativeAndFlatpakReferencesDeduplicateCanonicalRoots()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddGame(temp, "steam", 42, "Shared", "Shared", "Shared.exe");
        var link = temp.Combine("flatpak-link");
        Directory.CreateSymbolicLink(link, steam);

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam, link]);

        Assert.Single(result.SteamRoots);
        Assert.Single(result.Games);
        Assert.Contains(result.RootDiagnostics!, diagnostic => diagnostic.Deduplicated);
    }

    [Fact]
    public async Task InaccessibleExternalLibraryReportsDiagnosticWithoutHanging()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        var locked = temp.Directory("locked-library");
        temp.Directory("locked-library", "steamapps");
        temp.File("steam/steamapps/libraryfolders.vdf",
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} \"1\" {{ \"path\" \"{Escape(locked)}\" }} }}");
        AddGame(temp, "steam", 42, "Readable", "Readable", "Readable.exe");
        var steamApps = temp.Combine("locked-library", "steamapps");
        try
        {
            SetUnixMode(steamApps, UnixFileMode.None);
            var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam]);
            Assert.Equal(42u, Assert.Single(result.Games).AppId);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("not readable", StringComparison.OrdinalIgnoreCase) ||
                warning.Contains("permission", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.RootDiagnostics!, diagnostic =>
                diagnostic.CanonicalPath == Path.GetFullPath(locked) &&
                diagnostic is { Exists: true, Readable: false });
        }
        finally
        {
            SetUnixMode(steamApps, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task MissingRemovableLibraryAppearsWhenMountedLater()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        var removable = temp.Combine("removable");
        temp.Directory("steam", "steamapps");
        temp.File("steam/steamapps/libraryfolders.vdf",
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} \"1\" {{ \"path\" \"{Escape(removable)}\" }} }}");
        var service = new SteamDiscoveryService(new ExecutableDetector());

        var missing = await service.ScanAsync([steam]);
        Assert.Empty(missing.Games);
        Assert.Contains(missing.Warnings, warning => warning.Contains("unavailable", StringComparison.OrdinalIgnoreCase));

        Directory.CreateDirectory(removable);
        AddGame(temp, "removable", 99, "Removable Game", "Removable", "Removable.exe");
        Assert.Equal(99u, Assert.Single((await service.ScanAsync([steam])).Games).AppId);
    }

    [Fact]
    public async Task MalformedFallbackRootDoesNotAffectValidRoot()
    {
        using var temp = new TestDirectory();
        var valid = temp.Directory("valid");
        var malformed = temp.Directory("malformed", "steamapps");
        AddGame(temp, "valid", 42, "Valid", "Valid", "Valid.exe");
        temp.File("malformed/steamapps/libraryfolders.vdf", "\"libraryfolders\" {");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([valid, Path.GetDirectoryName(malformed)!]);

        Assert.Equal(42u, Assert.Single(result.Games).AppId);
        Assert.Contains(result.Warnings, warning => warning.Contains("libraryfolders.vdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PathsWithSpacesAndUnicodeAreSupported()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("Steam Root 游戏");
        var library = temp.Directory("External Library 日本語");
        temp.Directory(Path.GetRelativePath(temp.Path, steam), "steamapps");
        temp.File(Path.Combine(Path.GetRelativePath(temp.Path, steam), "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} \"1\" {{ \"path\" \"{Escape(library)}\" }} }}");
        AddGame(temp, Path.GetRelativePath(temp.Path, library), 123, "Unicode Game", "Unicode Game", "Game.exe");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam]);

        Assert.Equal(123u, Assert.Single(result.Games).AppId);
        Assert.Contains("Unicode Game", result.Games[0].GameRoot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyLibraryFoldersFormatIsParsed()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        var legacyLibrary = temp.Directory("legacy", "SteamLibrary");
        temp.Directory("steam", "steamapps");
        temp.File("steam/steamapps/libraryfolders.vdf",
            $"\"LibraryFolders\" {{\n\"TimeNextStatsReport\" \"0\"\n\"ContentStatsID\" \"0\"\n\"1\" \"{Escape(legacyLibrary)}\"\n}}");
        AddGame(temp, Path.GetRelativePath(temp.Path, legacyLibrary), 66, "Legacy Lib", "Legacy", "Legacy.exe");

        Assert.Equal(66u, Assert.Single((await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam])).Games).AppId);
    }

    [Fact]
    public async Task NonSteamDirectoryIsNotScannedForGames()
    {
        using var temp = new TestDirectory();
        var decoy = temp.Directory("not-steam");
        temp.Pe("not-steam/RandomGame.exe");
        temp.File("not-steam/game.acf", "\"AppState\" { \"appid\" \"1\" \"name\" \"Nope\" \"installdir\" \"Nope\" }");

        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([decoy]);

        Assert.Empty(result.Games);
        Assert.Empty(result.SteamRoots);
        Assert.Contains(result.RootDiagnostics!, diagnostic => !diagnostic.Exists);
    }

    [Fact]
    public async Task DiscoveryStaysFastWithManyNonexistentFallbackCandidates()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddGame(temp, "steam", 42, "Fast", "Fast", "Fast.exe");
        var missing = Enumerable.Range(0, 200)
            .Select(index => temp.Combine($"missing-root-{index}"))
            .ToArray();

        var started = DateTime.UtcNow;
        var result = await new SteamDiscoveryService(new ExecutableDetector()).ScanAsync([steam, .. missing]);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(42u, Assert.Single(result.Games).AppId);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Discovery took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public void DefaultRootCandidatesIncludeFlatpakAndSnapSources()
    {
        var candidates = SteamDiscoveryService.DiscoverRootCandidates(includeDefaultRoots: true);
        Assert.Contains(candidates, candidate => candidate.Source == SteamRootSource.Flatpak);
        Assert.Contains(candidates, candidate => candidate.Source == SteamRootSource.Snap);
        Assert.Contains(candidates, candidate => candidate.Source == SteamRootSource.Native);
        Assert.Contains(candidates, candidate => candidate.Source == SteamRootSource.Xdg);
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

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void SetUnixMode(string path, UnixFileMode mode) => File.SetUnixFileMode(path, mode);
}
