using System.IO.Compression;
using System.Net;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class ArtifactTests
{
    [Fact]
    public async Task RejectsArchivePathTraversal()
    {
        using var temp = new TestDirectory(); var zipPath = temp.Combine("bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { var entry = zip.CreateEntry("../escape.dll"); await using var writer = new StreamWriter(entry.Open()); await writer.WriteAsync("bad"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => ArtifactValidator.ExtractZipAsync(zipPath, temp.Directory("out")));
        Assert.False(File.Exists(temp.Combine("escape.dll")));
    }

    [Fact]
    public async Task ReleaseClientUsesConditionalRequestAndCache()
    {
        using var temp = new TestDirectory(); var handler = new FakeHandler(); var client = new HttpClient(handler);
        var env = new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") };
        var releases = new GitHubReleaseClient(client, new XdgPaths(temp.Path, env));
        var first = await releases.GetLatestAsync(ComponentKind.OptiScaler);
        var cached = await releases.GetLatestAsync(ComponentKind.OptiScaler);
        var refreshed = await releases.GetLatestAsync(ComponentKind.OptiScaler, forceRefresh: true);
        Assert.Equal("v1", first!.Version); Assert.Equal("v1", cached!.Version); Assert.Equal("v1", refreshed!.Version);
        Assert.Equal(2, handler.Count); Assert.True(handler.SawConditionalRequest);
    }

    [Fact]
    public async Task RemoteManifestRejectsInvalidUtf8WithoutCachingIt()
    {
        using var temp = new TestDirectory();
        var paths = new XdgPaths(temp.Path,
            new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var client = new RemoteManifestClient(
            new HttpClient(new ArtifactHandler([0xc3, 0x28])), paths);

        var check = await client.CheckAsync(true);

        Assert.Equal(MetadataCheckState.UnableToCheck, check.State);
        Assert.False(check.IsCached);
        Assert.Contains("UTF-8", check.Reason, StringComparison.Ordinal);
        Assert.Null(await client.ReadCatalogAsync());
    }

    [Fact]
    public async Task RemoteManifestRejectsOversizedBodyBeforeReadingIt()
    {
        using var temp = new TestDirectory();
        var paths = new XdgPaths(temp.Path,
            new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var client = new RemoteManifestClient(
            new HttpClient(new OversizedManifestHandler()), paths);

        var check = await client.CheckAsync(true);

        Assert.Equal(MetadataCheckState.UnableToCheck, check.State);
        Assert.False(check.IsCached);
        Assert.Contains("4 MiB", check.Reason, StringComparison.Ordinal);
        Assert.Null(await client.ReadCatalogAsync());
    }

    [Fact]
    public async Task StructuredCacheReusesValidatedArtifactWithoutDownloadingAgain()
    {
        using var temp = new TestDirectory(); var pe = await File.ReadAllBytesAsync(temp.Pe("source/addon.addon64"));
        var handler = new ArtifactHandler(pe); var http = new HttpClient(handler);
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot", new("https://clshortfuse.github.io/renodx/test.addon64"),
            "snapshot", "test.addon64", PeArchitecture.X64, 42, "test.addon64", null, ArtifactArchiveKind.None);

        var first = await cache.AcquireAsync(selection, http);
        var second = await cache.AcquireAsync(selection, http);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(ArtifactCacheState.Cached, second.CacheState);
        Assert.Equal(first.CachedPath, second.CachedPath);
        Assert.Single(await cache.ListAsync());
    }

    [Fact]
    public async Task DownloadRequiredSnapshotRefreshUsesNewPayloadAndRetainsPreviousOfflineVersion()
    {
        using var temp = new TestDirectory();
        var oldBytes = await File.ReadAllBytesAsync(temp.PeWithMarker("source/old.addon64", "old snapshot"));
        var newBytes = await File.ReadAllBytesAsync(temp.PeWithMarker("source/new.addon64", "new snapshot"));
        var paths = new XdgPaths(temp.Path,
            new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot",
            new("https://clshortfuse.github.io/renodx/rolling.addon64"), "snapshot",
            "rolling.addon64", PeArchitecture.X64, null, "rolling.addon64", null,
            ArtifactArchiveKind.None);
        var first = await cache.AcquireAsync(selection,
            new HttpClient(new TaggedArtifactHandler(oldBytes, "\"old\"")));
        var checkedSelection = (await cache.InspectAsync(selection)) with
        {
            CacheState = ArtifactCacheState.DownloadRequired
        };

        var refreshed = await cache.AcquireAsync(checkedSelection,
            new HttpClient(new TaggedArtifactHandler(newBytes, "\"new\"")));

        Assert.Equal("\"new\"", refreshed.ETag);
        Assert.NotEqual(first.Sha256, refreshed.Sha256);
        Assert.Equal(newBytes, await File.ReadAllBytesAsync(refreshed.CachedPath!));
        var snapshots = (await cache.ListAsync()).Where(entry =>
            entry.Metadata.Component == ComponentKind.RenoDx).ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.Contains(snapshots, entry => entry.Metadata.Sha256 == first.Sha256);
        Assert.Contains(snapshots, entry => entry.Metadata.Sha256 == refreshed.Sha256);

        _ = await cache.CleanupAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1);
        Assert.True(File.Exists(cache.BlobPath(first.Sha256!)));
        Assert.True(File.Exists(cache.BlobPath(refreshed.Sha256!)));
        var offline = await cache.InspectAsync(selection);
        Assert.Equal(refreshed.Sha256, offline.Sha256);
        Assert.Equal("\"new\"", offline.ETag);
    }

    [Fact]
    public async Task CorruptDownloadNeverBecomesAValidCacheEntry()
    {
        using var temp = new TestDirectory();
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot", new("https://clshortfuse.github.io/renodx/bad.addon64"),
            "snapshot", "bad.addon64", PeArchitecture.X64, 42, "bad.addon64", null, ArtifactArchiveKind.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.AcquireAsync(selection, new HttpClient(new ArtifactHandler("not a PE"u8.ToArray()))));

        Assert.Empty(await cache.ListAsync());
        Assert.Empty(Directory.Exists(Path.Combine(cache.Root, "downloads"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(cache.Root, "downloads")) : []);
    }

    [Fact]
    public async Task CacheRejectsPayloadWhoseArchitectureDoesNotMatchSelection()
    {
        using var temp = new TestDirectory();
        var x86Bytes = await File.ReadAllBytesAsync(temp.Pe("source/wrong.addon64", 0x014c));
        var paths = new XdgPaths(temp.Path,
            new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot",
            new("https://clshortfuse.github.io/renodx/wrong.addon64"), "snapshot",
            "wrong.addon64", PeArchitecture.X64, null, "wrong.addon64", null,
            ArtifactArchiveKind.None);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.AcquireAsync(selection, new HttpClient(new ArtifactHandler(x86Bytes))));

        Assert.Contains("X64 is required", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await cache.ListAsync());
    }

    [Fact]
    public async Task ChangedCachedPayloadIsReportedInvalid()
    {
        using var temp = new TestDirectory(); var pe = await File.ReadAllBytesAsync(temp.Pe("source/addon.addon64"));
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot", new("https://clshortfuse.github.io/renodx/test.addon64"),
            "snapshot", "test.addon64", PeArchitecture.X64, 42, "test.addon64", null, ArtifactArchiveKind.None);
        var acquired = await cache.AcquireAsync(selection, new HttpClient(new ArtifactHandler(pe)));
        await File.AppendAllTextAsync(acquired.CachedPath!, "changed");

        var inspected = await cache.InspectAsync(selection);

        Assert.Equal(ArtifactCacheState.Invalid, inspected.CacheState);
        Assert.False(Assert.Single(await cache.ListAsync()).IsValid);
    }

    [Fact]
    public async Task InterruptedDownloadLeavesNoPartialCacheEntry()
    {
        using var temp = new TestDirectory(); var pe = await File.ReadAllBytesAsync(temp.Pe("source/addon.addon64"));
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot", new("https://clshortfuse.github.io/renodx/interrupted.addon64"),
            "snapshot", "interrupted.addon64", PeArchitecture.X64, 42, "interrupted.addon64", null, ArtifactArchiveKind.None);

        await Assert.ThrowsAsync<IOException>(() => cache.AcquireAsync(selection, new HttpClient(new InterruptedHandler(pe))));

        Assert.Empty(await cache.ListAsync());
        Assert.Empty(Directory.Exists(Path.Combine(cache.Root, "downloads"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(cache.Root, "downloads")) : []);
    }

    [Fact]
    public async Task OversizedDownloadIsRejectedBeforeItCanEnterTheContentCache()
    {
        using var temp = new TestDirectory();
        var paths = new XdgPaths(temp.Path,
            new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot",
            new("https://clshortfuse.github.io/renodx/oversized.addon64"), "snapshot",
            "oversized.addon64", PeArchitecture.X64, null, "oversized.addon64", null,
            ArtifactArchiveKind.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.AcquireAsync(
            selection, new HttpClient(new OversizedHandler())));

        Assert.Empty(await cache.ListAsync());
        Assert.Empty(Directory.Exists(cache.BlobRoot)
            ? Directory.EnumerateFiles(cache.BlobRoot, "*", SearchOption.AllDirectories) : []);
        Assert.Empty(Directory.Exists(cache.StagingRoot)
            ? Directory.EnumerateFileSystemEntries(cache.StagingRoot) : []);
    }

    [Fact]
    public async Task CentralResolverSelectsOfficialExactProfileAndDependencies()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, 1091500, "Cyberpunk 2077", GameEngine.Unknown);
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var handler = new ResolverHandler();

        var result = await new OfficialArtifactResolver(new HttpClient(handler), paths).ResolveAsync(game);

        var reno = Assert.Single(result.Components, x => x.Component == ComponentKind.RenoDx);
        Assert.Equal(ArtifactSupportKind.ExactGameProfile, reno.Support);
        Assert.Equal("renodx-cp2077.addon64", reno.AssetFileName);
        Assert.Contains(result.Components, x => x.Component == ComponentKind.ReShade && x.Version == "6.7.3");
        Assert.Contains(result.Components, x => x.Component == ComponentKind.OptiScaler && x.AssetFileName == "OptiScaler.7z");
        Assert.DoesNotContain(result.Components, x => x.Component == ComponentKind.OptiPatcher);
        Assert.True(result.IsFullyAutomatic);
    }

    [Fact]
    public async Task RemoteManifestAndWikiExactMappingsResolveAndReuseValidatedOfflineCatalogs()
    {
        using var temp = new TestDirectory();
        var paths = new XdgPaths(temp.Path,
            new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var handler = new RemoteCatalogHandler();
        var exactGame = Game(temp, 424242, "Unlisted Steam Name", GameEngine.Unknown, "RemoteGame.exe");

        var online = await new OfficialArtifactResolver(new HttpClient(handler), paths)
            .ResolveAsync(exactGame, true);

        var exact = Assert.Single(online.Artifacts, artifact => artifact.Component == ComponentKind.RenoDx);
        Assert.EndsWith("renodx-remote.addon64", exact.SourceUrl.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(ArtifactSupportKind.ExactGameProfile, exact.Support);
        Assert.Equal(exactGame.AppId, exact.GameAppId);
        Assert.True(exact.SourceValidatedByOfficialMetadata);
        Assert.True(online.Profile.ExactAppId);

        var offlineResolver = new OfficialArtifactResolver(new HttpClient(new FailingHandler()), paths);
        var offline = await offlineResolver.ResolveAsync(exactGame, false);
        Assert.Equal(MetadataCheckState.Offline, offline.MetadataState);
        Assert.Equal(exact.SourceUrl, Assert.Single(offline.Artifacts,
            artifact => artifact.Component == ComponentKind.RenoDx).SourceUrl);

        var aliasGame = Game(temp, 424243, "Remote Alias", GameEngine.Unknown, "AliasGame.exe");
        var alias = await offlineResolver.ResolveAsync(aliasGame, false);
        var aliasSelection = Assert.Single(alias.Artifacts,
            artifact => artifact.Component == ComponentKind.RenoDx);
        Assert.Equal(ArtifactSupportKind.ExecutableOrAliasProfile, aliasSelection.Support);
        Assert.False(alias.Profile.ExactAppId);

        var unityGame = Game(temp, 424244, "Outer Wilds", GameEngine.Unity, "OuterWilds.exe");
        var unity = await offlineResolver.ResolveAsync(unityGame, false);
        var unitySelection = Assert.Single(unity.Artifacts,
            artifact => artifact.Component == ComponentKind.RenoDx);
        Assert.Equal(ArtifactSupportKind.UnityFallback, unitySelection.Support);
        Assert.Null(unitySelection.GameAppId);
    }

    [Fact]
    public async Task DynamicExactRenoDxInstallRetainsDetectionProvenanceAndAppIdScopedCache()
    {
        using var temp = new TestDirectory();
        var paths = new XdgPaths(temp.Path,
            new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var game = Game(temp, 424242, "Unlisted Steam Name", GameEngine.Unity, "RemoteGame.exe");
        var cache = new ArtifactCacheService(paths);
        var reshadeBytes = await File.ReadAllBytesAsync(
            temp.PeWithMarker("source/ReShade64.dll", "Searching for add-ons"));
        var reshade = await cache.AcquireAsync(new ArtifactSelection(ComponentKind.ReShade, "6.7.3",
            new("https://reshade.me/ReShade64.dll"), "6.7.3", "ReShade64.dll", PeArchitecture.X64,
            null, "ReShade64.dll", null, ArtifactArchiveKind.None),
            new HttpClient(new ArtifactHandler(reshadeBytes)));
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(
            game, CachedArtifactMaterializer.MaterializeSingle(reshade)), false)).Succeeded);

        var addonBytes = await File.ReadAllBytesAsync(
            temp.PeWithMarker("source/renodx-remote.addon64", "dynamic exact"));
        var resolver = new OfficialArtifactResolver(
            new HttpClient(new RemoteCatalogHandler(addonBytes)), paths);
        var resolution = await resolver.ResolveAsync(game, true);
        var acquired = await resolver.AcquireSelectedAsync(resolution,
            new HashSet<ComponentKind> { ComponentKind.RenoDx }, true);
        var exact = Assert.Single(acquired.Artifacts,
            artifact => artifact.Component == ComponentKind.RenoDx);

        Assert.Equal(ArtifactCacheState.Cached, exact.CacheState);
        Assert.Equal(game.AppId, exact.GameAppId);
        var exactMetadata = Assert.Single((await cache.ListAsync()), entry =>
            entry.Metadata.Component == ComponentKind.RenoDx);
        Assert.Equal(game.AppId, exactMetadata.Metadata.GameAppId);
        var install = await planner.BuildInstallPlanAsync(
            game, CachedArtifactMaterializer.MaterializeSingle(exact));
        Assert.True((await executor.ExecuteAsync(install, false)).Succeeded);
        var detected = Assert.Single(await new ComponentDetector().DetectAsync(game),
            component => component.Component == ComponentKind.RenoDx);
        Assert.Equal(ComponentHealth.Installed, detected.Health);

        var offlineResolver = new OfficialArtifactResolver(new HttpClient(new FailingHandler()), paths);
        var offline = await offlineResolver.ResolveAsync(game, false);
        Assert.Equal(ArtifactCacheState.Cached, Assert.Single(offline.Artifacts,
            artifact => artifact.Component == ComponentKind.RenoDx).CacheState);
        var otherGame = Game(temp, 424243, "Unlisted Steam Name", GameEngine.Unity, "RemoteGame.exe");
        var other = await offlineResolver.ResolveAsync(otherGame, false);
        var otherSelection = Assert.Single(other.Artifacts,
            artifact => artifact.Component == ComponentKind.RenoDx);
        Assert.Equal((uint)424243, otherSelection.GameAppId);
        Assert.Equal(ArtifactCacheState.DownloadRequired, otherSelection.CacheState);
    }

    [Fact]
    public async Task OfflineResolutionReusesValidatedRequiredFiles()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, 3668370, "Night Swarm", GameEngine.Unity);
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var reshadeBytes = await File.ReadAllBytesAsync(temp.PeWithMarker("source/ReShade64.dll", "Searching for add-ons"));
        var renoBytes = await File.ReadAllBytesAsync(temp.Pe("source/renodx-unityengine.addon64"));
        await cache.AcquireAsync(new ArtifactSelection(ComponentKind.ReShade, "6.7.3", new("https://reshade.me/ReShade64.dll"),
            "6.7.3", "ReShade64.dll", PeArchitecture.X64, null, "ReShade64.dll", null, ArtifactArchiveKind.None),
            new HttpClient(new ArtifactHandler(reshadeBytes)));
        await cache.AcquireAsync(new ArtifactSelection(ComponentKind.RenoDx, "snapshot",
            new("https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64"), "snapshot",
            "renodx-unityengine.addon64", PeArchitecture.X64, null, "renodx-unityengine.addon64", null, ArtifactArchiveKind.None,
            GameProfile: "steam-3668370-night-swarm", Support: ArtifactSupportKind.UnityFallback),
            new HttpClient(new ArtifactHandler(renoBytes)));

        var result = await new OfficialArtifactResolver(new HttpClient(new FailingHandler()), paths).AcquireAsync(game, false);

        Assert.True(result.IsFullyAutomatic);
        Assert.All(result.Artifacts, artifact => Assert.Equal(ArtifactCacheState.Cached, artifact.CacheState));
        Assert.Equal(MetadataCheckState.Offline, result.MetadataState);
    }

    [Fact]
    public async Task RenoDxAcquisitionFailureDoesNotDiscardCachedOptiScaler()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, 3668370, "Night Swarm", GameEngine.Unity);
        var paths = new XdgPaths(temp.Path,
            new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var profile = await new GameProfileCatalog(paths).MatchAsync(game);
        var reshadePath = temp.PeWithMarker("cached/ReShade64.dll", "Searching for add-ons");
        var optiPath = temp.PeWithMarker("cached/OptiScaler.dll", "OptiScaler");
        var reshade = new ArtifactSelection(ComponentKind.ReShade, "6.7.3",
            new("https://reshade.me/downloads/ReShade_Setup_6.7.3_Addon.exe"), "6.7.3",
            "ReShade_Setup_6.7.3_Addon.exe", PeArchitecture.X64, null, "ReShade64.dll", null,
            ArtifactArchiveKind.ReShadeInstaller, ArtifactCacheState.Cached, reshadePath,
            Validation: ArtifactValidationState.Valid, Sha256: await ArtifactDownloader.Sha256Async(reshadePath));
        var reno = new ArtifactSelection(ComponentKind.RenoDx, "snapshot",
            new("https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64"),
            "snapshot", "renodx-unityengine.addon64", PeArchitecture.X64, null,
            "renodx-unityengine.addon64", null, ArtifactArchiveKind.None,
            GameProfile: profile.Profile.Id, Support: ArtifactSupportKind.UnityFallback);
        var opti = new ArtifactSelection(ComponentKind.OptiScaler, "v0.9.4",
            new("https://github.com/optiscaler/OptiScaler/releases/download/v0.9.4/OptiScaler.7z"),
            "v0.9.4", "OptiScaler.7z", PeArchitecture.X64, null, "OptiScaler.dll", null,
            ArtifactArchiveKind.SevenZip, ArtifactCacheState.Cached, optiPath,
            Validation: ArtifactValidationState.Valid, Sha256: await ArtifactDownloader.Sha256Async(optiPath));
        var resolution = new GameArtifactResolution(profile, [reshade, reno, opti], [], true,
            MetadataCheckState.Online)
        {
            CanAcquireRenoSetup = true,
            CanAcquireOptiScaler = true
        };
        var resolver = new OfficialArtifactResolver(new HttpClient(new FailingHandler()), paths);

        var acquired = await resolver.AcquireSelectedAsync(resolution,
            new HashSet<ComponentKind> { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler });

        Assert.False(acquired.CanAcquireRenoSetup);
        Assert.True(acquired.CanAcquireOptiScaler);
        Assert.True(acquired.IsFullyAutomatic);
        Assert.Equal(ArtifactCacheState.Cached,
            Assert.Single(acquired.Artifacts, x => x.Component == ComponentKind.OptiScaler).CacheState);
        Assert.Contains(acquired.Warnings, warning =>
            warning.Contains("RenoDx acquisition failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedSnapshotMetadataReportsUpdateAvailable()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, 3668370, "Night Swarm", GameEngine.Unity);
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var cache = new ArtifactCacheService(paths);
        var bytes = await File.ReadAllBytesAsync(temp.Pe("source/renodx-unityengine.addon64"));
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot",
            new("https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64"), "snapshot",
            "renodx-unityengine.addon64", PeArchitecture.X64, null, "renodx-unityengine.addon64", null, ArtifactArchiveKind.None,
            GameProfile: "steam-3668370-night-swarm", Support: ArtifactSupportKind.UnityFallback);
        await cache.AcquireAsync(selection, new HttpClient(new TaggedArtifactHandler(bytes, "\"old\"")));
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var reshadeInstall = await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, temp.PeWithMarker("install/ReShade64.dll", "reshade.me"),
                "ReShade64.dll", "6.7.3"));
        Assert.True((await executor.ExecuteAsync(reshadeInstall, false)).Succeeded);
        var install = await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.RenoDx, temp.Pe("install/renodx-unityengine.addon64"), "renodx-unityengine.addon64", "snapshot"));
        Assert.True((await executor.ExecuteAsync(install, false)).Succeeded);

        var report = await new StackStatusService(new HttpClient(new SnapshotUpdateHandler()), paths).GetAsync(game, true);

        Assert.Equal(ComponentHealth.Outdated, Assert.Single(report.Components, x => x.Component == ComponentKind.RenoDx).Health);
        Assert.Equal(MetadataCheckState.Online, report.ArtifactResolution.MetadataState);
    }

    private static SteamGame Game(
        TestDirectory temp,
        uint appId,
        string name,
        GameEngine engine,
        string executableName = "Game.exe")
    {
        var root = temp.Directory($"game-{appId}");
        var executable = temp.Pe($"game-{appId}/{executableName}");
        return new(appId, name, temp.Path, temp.Path, root, temp.Combine("pfx", appId.ToString()), executable, root,
            DetectionConfidence.High, "fixture", engine,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, new FileInfo(executable).Length, ["fixture"])]);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public bool SawConditionalRequest { get; private set; }
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            if (request.Headers.IfNoneMatch.Count > 0) { SawConditionalRequest = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"tag_name\":\"v1\",\"html_url\":\"https://github.com/optiscaler/OptiScaler/releases/tag/v1\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[]}") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\""); return Task.FromResult(response);
        }
    }

    private sealed class ArtifactHandler(byte[] content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class InterruptedHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(content)) });
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = ArtifactValidator.MaxDownloadSizeBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class OversizedManifestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 8L * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ResolverHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            if (request.RequestUri!.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<a href=\"/downloads/ReShade_Setup_6.7.3_Addon.exe\">full add-on support</a>")
                });
            if (request.RequestUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":1}") });
            var patcher = request.RequestUri.AbsolutePath.Contains("OptiPatcher", StringComparison.OrdinalIgnoreCase);
            var json = patcher
                ? "{\"tag_name\":\"rolling\",\"html_url\":\"https://github.com/optiscaler/OptiPatcher/releases/tag/rolling\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[{\"name\":\"OptiPatcher.asi\",\"browser_download_url\":\"https://github.com/optiscaler/OptiPatcher/releases/download/rolling/OptiPatcher.asi\",\"size\":42}]}"
                : "{\"tag_name\":\"v1.2.3\",\"html_url\":\"https://github.com/optiscaler/OptiScaler/releases/tag/v1.2.3\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[{\"name\":\"OptiScaler.7z\",\"browser_download_url\":\"https://github.com/optiscaler/OptiScaler/releases/download/v1.2.3/OptiScaler.7z\",\"size\":42}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class TaggedArtifactHandler(byte[] content, string etag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return Task.FromResult(response);
        }
    }

    private sealed class SnapshotUpdateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new\"");
                return Task.FromResult(response);
            }
            if (request.RequestUri!.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<a href=\"/downloads/ReShade_Setup_6.7.3_Addon.exe\">full add-on support</a>")
                });
            if (request.RequestUri == RenoDxWikiClient.SourceUri)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        # List
                        | Name | Links | Status |
                        | --- | --- | --- |
                        | Night Swarm | [Snapshot](https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64) | :white_check_mark: |
                        """)
                });
            if (request.RequestUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":1}") });
            if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tag_name\":\"v1\",\"html_url\":\"https://github.com/optiscaler/OptiScaler/releases/tag/v1\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[]}")
                });
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }
    }

    private sealed class RemoteCatalogHandler(byte[]? addonPayload = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri == RenoDxWikiClient.SourceUri)
                return Task.FromResult(Response("""
                    # List
                    | Name | Links | Status |
                    | --- | --- | --- |
                    | Remote Canonical | [Snapshot](https://author.github.io/renodx/renodx-wiki.addon64) | :white_check_mark: |
                    | Outer Wilds | [Snapshot](https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64) | :white_check_mark: |
                    """, "\"wiki-v1\""));
            if (request.RequestUri!.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Response("""
                    {
                      "version": 2,
                      "steamAppIdOverrides": { "Remote Canonical": 424242 },
                      "wikiNameOverrides": { "Remote Alias": "Remote Canonical", "RemoteGame": "Remote Canonical" },
                      "launchExeOverrides": { "Remote Canonical": "RemoteGame.exe" },
                      "snapshotOverrides": {
                        "Remote Canonical": "https://author.github.io/renodx/renodx-remote.addon64",
                        "Outer Wilds": "https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64"
                      }
                    }
                    """, "\"manifest-v2\""));
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(Response(string.Empty, "\"snapshot-v1\""));
            if (addonPayload is not null &&
                request.RequestUri.Host.Equals("author.github.io", StringComparison.OrdinalIgnoreCase))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(addonPayload)
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"snapshot-v1\"");
                return Task.FromResult(response);
            }
            if (request.RequestUri.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Response("<html>No current fixture release</html>"));
            if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Response(
                    "{\"tag_name\":\"v1\",\"html_url\":\"https://github.com/optiscaler/OptiScaler/releases/tag/v1\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[]}"));
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Response(string content, string? etag = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
            if (etag is not null)
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return response;
        }
    }

    private sealed class InterruptedStream(byte[] content) : Stream
    {
        private bool returnedData;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (returnedData) throw new IOException("simulated interrupted download");
            returnedData = true;
            var length = Math.Min(count, content.Length / 2);
            Array.Copy(content, 0, buffer, offset, length);
            return length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (returnedData) return ValueTask.FromException<int>(new IOException("simulated interrupted download"));
            returnedData = true;
            var length = Math.Min(buffer.Length, content.Length / 2);
            content.AsMemory(0, length).CopyTo(buffer);
            return ValueTask.FromResult(length);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
