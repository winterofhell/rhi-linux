using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class BundleAndCacheTests
{
    [Fact]
    public async Task OfficialBundleManifestSelectsRuntimeLayoutWithoutSetupScripts()
    {
        using var temp = new TestDirectory();
        var root = CreateBundle(temp, "bundle");

        var manifest = await OptiScalerBundleParser.ParseAsync(root, "v0.9.4", "v0.9.4", "OptiScaler.7z", new string('a', 64));

        Assert.Equal("OptiScaler.ini", manifest.DefaultIniPath);
        Assert.Contains(manifest.RuntimeFiles, x => x.RelativePath == "D3D12_Optiscaler/D3D12Core.dll");
        Assert.DoesNotContain(manifest.RuntimeFiles, x => x.RelativePath == "plugins/future-runtime.dll");
        Assert.DoesNotContain(manifest.RuntimeFiles, x => x.RelativePath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifest.RuntimeFiles, x => x.RelativePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DeploymentFileRequirement.Required,
            Assert.Single(manifest.Files, x => x.RelativePath == "OptiScaler.dll").Requirement);
        Assert.Equal(DeploymentFileRequirement.Conditional,
            Assert.Single(manifest.Files, x => x.RelativePath == "amd_fidelityfx_dx12.dll").Requirement);
        Assert.Equal("fidelityfx-dx12",
            Assert.Single(manifest.Files, x => x.RelativePath == "amd_fidelityfx_dx12.dll").Feature);
        Assert.Equal(DeploymentFileRequirement.Conditional,
            Assert.Single(manifest.Files, x => x.RelativePath == "D3D12_Optiscaler/D3D12Core.dll").Requirement);
        Assert.Equal(DeploymentFileRequirement.UnrelatedArchiveContent,
            Assert.Single(manifest.Files, x => x.RelativePath == "plugins/future-runtime.dll").Requirement);
        Assert.All(manifest.Files, x => Assert.Equal(64, x.Sha256.Length));
    }

    [Fact]
    public async Task UnknownBundleLayoutIsRejectedBeforePlanning()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("unknown");
        temp.File("unknown/OptiScaler.dll", "binary");
        temp.File("unknown/OptiScaler.ini", "[Plugins]\nLoadReshade=false\n");

        await Assert.ThrowsAsync<InvalidDataException>(() => OptiScalerBundleParser.ParseAsync(
            root, "v2.0.0", "v2.0.0", "future.7z", new string('b', 64)));
    }

    [Fact]
    public async Task LegacySchemaOneBundleManifestIsReclassifiedWhenReadFromCache()
    {
        using var temp = new TestDirectory();
        var path = temp.File("manifest.json", $$"""
            {
              "schemaVersion": 1,
              "releaseVersion": "v0.9.4",
              "releaseTag": "v0.9.4",
              "archiveAssetName": "OptiScaler.7z",
              "archiveSha256": "{{new string('a', 64)}}",
              "containedPaths": ["OptiScaler.dll", "OptiScaler.ini", "amd_fidelityfx_dx12.dll", "readme.txt"],
              "files": [
                { "relativePath": "OptiScaler.dll", "sha256": "{{new string('b', 64)}}", "size": 10, "runtimeRequired": true, "role": "runtime" },
                { "relativePath": "OptiScaler.ini", "sha256": "{{new string('c', 64)}}", "size": 10, "runtimeRequired": true, "role": "config" },
                { "relativePath": "amd_fidelityfx_dx12.dll", "sha256": "{{new string('d', 64)}}", "size": 10, "runtimeRequired": true, "role": "runtime" },
                { "relativePath": "readme.txt", "sha256": "{{new string('e', 64)}}", "size": 10, "runtimeRequired": false, "role": "docs" }
              ],
              "defaultIniPath": "OptiScaler.ini",
              "supportedProxyNames": ["dxgi.dll"],
              "setupScriptDecisions": [],
              "bundledAuxiliaryComponents": [],
              "obsoleteManagedPaths": []
            }
            """);

        var manifest = await OptiScalerBundleParser.ReadAsync(path);

        Assert.Equal(DeploymentFileRequirement.Required,
            manifest.Files.Single(file => file.RelativePath == "OptiScaler.dll").Requirement);
        var fidelityFx = manifest.Files.Single(file => file.RelativePath == "amd_fidelityfx_dx12.dll");
        Assert.Equal(DeploymentFileRequirement.Conditional, fidelityFx.Requirement);
        Assert.Equal("fidelityfx-dx12", fidelityFx.Feature);
        Assert.True(fidelityFx.CanOmitOnCollision);
        Assert.Equal(DeploymentFileRequirement.UnrelatedArchiveContent,
            manifest.Files.Single(file => file.RelativePath == "readme.txt").Requirement);
    }

    [Fact]
    public async Task SharedContentCacheDeduplicatesAcrossGameProfiles()
    {
        using var temp = new TestDirectory();
        var bytes = await File.ReadAllBytesAsync(temp.Pe("source/shared.addon64"));
        var handler = new CountingHandler(bytes);
        var paths = Paths(temp);
        var cache = new ArtifactCacheService(paths);
        ArtifactSelection Selection(string profile) => new(ComponentKind.RenoDx, "snapshot",
            new("https://clshortfuse.github.io/renodx/shared.addon64"), "snapshot", "shared.addon64",
            PeArchitecture.X64, null, "shared.addon64", null, ArtifactArchiveKind.None,
            GameProfile: profile, Support: ArtifactSupportKind.UnityFallback);

        var first = await cache.AcquireAsync(Selection("game-a"), new HttpClient(handler));
        var second = await cache.AcquireAsync(Selection("game-b"), new HttpClient(handler));

        Assert.Equal(1, handler.Count);
        Assert.Equal(first.CachedPath, second.CachedPath);
        Assert.Single(Directory.EnumerateFiles(cache.BlobRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExactArtifactMetadataIsScopedToItsSteamAppId()
    {
        using var temp = new TestDirectory();
        var bytes = await File.ReadAllBytesAsync(temp.Pe("source/exact.addon64"));
        var cache = new ArtifactCacheService(Paths(temp));
        ArtifactSelection Selection(uint appId) => new(ComponentKind.RenoDx, "snapshot",
            new("https://clshortfuse.github.io/renodx/exact.addon64"), "snapshot", "exact.addon64",
            PeArchitecture.X64, appId, "exact.addon64", null, ArtifactArchiveKind.None,
            GameProfile: $"game-{appId}", Support: ArtifactSupportKind.ExactGameProfile);

        _ = await cache.AcquireAsync(Selection(10), new HttpClient(new CountingHandler(bytes)));
        var otherGame = await cache.InspectAsync(Selection(20));

        Assert.Equal(ArtifactCacheState.DownloadRequired, otherGame.CacheState);
        Assert.Equal((uint)10, Assert.Single(await cache.ListAsync()).Metadata.GameAppId);
    }

    [Fact]
    public async Task CorruptBlobIsQuarantinedAndStagingIsRemoved()
    {
        using var temp = new TestDirectory();
        var bytes = await File.ReadAllBytesAsync(temp.Pe("source/shared.addon64"));
        var cache = new ArtifactCacheService(Paths(temp));
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot",
            new("https://clshortfuse.github.io/renodx/shared.addon64"), "snapshot", "shared.addon64",
            PeArchitecture.X64, null, "shared.addon64", null, ArtifactArchiveKind.None);
        var acquired = await cache.AcquireAsync(selection, new HttpClient(new CountingHandler(bytes)));
        await File.AppendAllTextAsync(acquired.CachedPath!, "corrupt");

        var verified = await cache.VerifyAsync();

        Assert.Contains(verified, x => !x.IsValid);
        Assert.NotEmpty(Directory.EnumerateFiles(cache.QuarantineRoot));
        Assert.Empty(Directory.Exists(cache.StagingRoot) ? Directory.EnumerateFileSystemEntries(cache.StagingRoot) : []);
    }

    [Fact]
    public async Task CleanupPreservesInstalledReferencesAndRemovesUnreferencedBlob()
    {
        using var temp = new TestDirectory();
        var cache = new ArtifactCacheService(Paths(temp));
        var retainedBytes = "retained"u8.ToArray();
        var unusedBytes = "unused"u8.ToArray();
        var retainedHash = Hash(retainedBytes);
        var unusedHash = Hash(unusedBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(cache.BlobPath(retainedHash))!);
        await File.WriteAllBytesAsync(cache.BlobPath(retainedHash), retainedBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(cache.BlobPath(unusedHash))!);
        await File.WriteAllBytesAsync(cache.BlobPath(unusedHash), unusedBytes);

        await cache.CleanupAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { retainedHash }, 1);

        Assert.True(File.Exists(cache.BlobPath(retainedHash)));
        Assert.False(File.Exists(cache.BlobPath(unusedHash)));
    }

    [Fact]
    public async Task CleanupRetainsCurrentAndPreviousReleasePerComponent()
    {
        using var temp = new TestDirectory();
        var cache = new ArtifactCacheService(Paths(temp));
        foreach (var version in new[] { "v0.9.2", "v0.9.3", "v0.9.4" })
        {
            var bytes = await File.ReadAllBytesAsync(temp.Pe($"source/{version}/renodx.addon64",
                size: version == "v0.9.2" ? 1024 : version == "v0.9.3" ? 1280 : 1536));
            var selection = new ArtifactSelection(ComponentKind.RenoDx, version,
                new($"https://clshortfuse.github.io/renodx/{version}/renodx.addon64"), version,
                "renodx.addon64", PeArchitecture.X64, null, "renodx.addon64", null,
                ArtifactArchiveKind.None);
            _ = await cache.AcquireAsync(selection, new HttpClient(new CountingHandler(bytes)));
            await Task.Delay(5);
        }

        var cleanup = await cache.CleanupAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1);
        var retained = (await cache.ListAsync()).Select(x => x.Metadata.Version).Order().ToArray();

        Assert.Equal(["v0.9.3", "v0.9.4"], retained);
        Assert.Equal(1, cleanup.ReleasesRemoved);
    }

    [Fact]
    public async Task RecommendedBundlePreservesSubdirectoriesAndCleansLegacyManagedPatcher()
    {
        using var temp = new TestDirectory();
        var gameRoot = temp.Directory("game");
        var exe = temp.Pe("game/Game.exe");
        var game = new SteamGame(42, "Game", temp.Path, temp.Path, gameRoot, temp.Combine("pfx"), exe,
            gameRoot, DetectionConfidence.High, "fixture", GameEngine.Unknown, []);
        var patcher = temp.Pe("game/plugins/OptiPatcher.asi");
        var patcherHash = await ArtifactDownloader.Sha256Async(patcher);
        var state = Path.Combine(gameRoot, ".rhi-linux", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        await File.WriteAllTextAsync(state, JsonSerializer.Serialize(new GameManifest
        {
            AppId = 42,
            Files = [new(Path.GetRelativePath(gameRoot, patcher), ComponentKind.OptiPatcher, patcherHash, "legacy", null, null)]
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var ini = temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n");
        var nested = temp.Pe("stage/D3D12Core.dll");
        var identicalLegacy = Path.Combine(gameRoot, "D3D12_Optiscaler", "D3D12Core.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(identicalLegacy)!);
        File.Copy(nested, identicalLegacy);
        var main = temp.Pe("stage/OptiScaler.dll");
        var archiveHash = new string('c', 64);
        var bundle = new OptiScalerBundleManifest(1, "v0.9.4", "v0.9.4", "OptiScaler.7z", archiveHash,
            ["OptiScaler.dll", "OptiScaler.ini", "D3D12_Optiscaler/D3D12Core.dll", "setup_linux.sh"], [],
            "OptiScaler.ini", DeploymentPlanner.SupportedProxyNames, [], [], ["nvapi64.dll", "nvngx.dll"]);
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, main, "OptiScaler.dll", "v0.9.4", Sha256: HashFile(main),
                RelativePath: "OptiScaler.dll", SourceBlobSha256: HashFile(main), SourceBundleSha256: archiveHash),
            [new(ComponentKind.OptiScaler, ini, "OptiScaler.ini", "v0.9.4", Sha256: HashFile(ini),
                 RelativePath: "OptiScaler.ini", SourceBlobSha256: HashFile(ini), SourceBundleSha256: archiveHash),
                new(ComponentKind.OptiScaler, nested, "D3D12Core.dll", "v0.9.4", Sha256: HashFile(nested),
                 RelativePath: "D3D12_Optiscaler/D3D12Core.dll", SourceBlobSha256: HashFile(nested), SourceBundleSha256: archiveHash)], bundle);

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(gameRoot, "D3D12_Optiscaler", "D3D12Core.dll")));
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.TrackExistingFile &&
            x.Target.Equals(identicalLegacy, StringComparison.Ordinal));
        Assert.False(File.Exists(patcher));
        Assert.DoesNotContain(plan.Operations, x => x.Type == DeploymentOperationType.Copy &&
            x.Target.EndsWith("setup_linux.sh", StringComparison.OrdinalIgnoreCase));
        var installed = await ComponentDetector.LoadManifestAsync(gameRoot);
        Assert.All(installed.Files.Where(x => x.Component == ComponentKind.OptiScaler),
            x => Assert.Equal(archiveHash, x.SourceBundleSha256));
    }

    [Fact]
    public async Task OptiPatcherCannotBeNewlyDeployed()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("game");
        var exe = temp.Pe("game/Game.exe");
        var game = new SteamGame(42, "Game", temp.Path, temp.Path, root, temp.Combine("pfx"), exe, root,
            DetectionConfidence.High, "fixture", GameEngine.Unknown, []);
        var artifact = new ComponentArtifact(ComponentKind.OptiPatcher, temp.Pe("stage/OptiPatcher.asi"), "OptiPatcher.asi");

        await Assert.ThrowsAsync<NotSupportedException>(() => new DeploymentPlanner().BuildInstallPlanAsync(game, artifact));
    }

    private static string CreateBundle(TestDirectory temp, string relative)
    {
        var root = temp.Directory(relative);
        temp.File($"{relative}/OptiScaler.dll", "OptiScaler");
        temp.File($"{relative}/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n");
        temp.File($"{relative}/setup_linux.sh", "proxies='dxgi.dll winmm.dll version.dll dbghelp.dll d3d12.dll wininet.dll winhttp.dll'\nDxgi=false\n");
        temp.File($"{relative}/setup_windows.bat", "rem dxgi.dll winmm.dll version.dll dbghelp.dll d3d12.dll wininet.dll winhttp.dll\n");
        foreach (var file in new[] { "fakenvapi.dll", "fakenvapi.ini", "amd_fidelityfx_dx12.dll",
                     "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_vk.dll",
                     "D3D12_Optiscaler/D3D12Core.dll", "plugins/future-runtime.dll" })
            temp.File($"{relative}/{file}", file);
        temp.File($"{relative}/readme.txt", "documentation");
        return root;
    }

    private static XdgPaths Paths(TestDirectory temp) => new(temp.Path,
        new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));

    private sealed class CountingHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
