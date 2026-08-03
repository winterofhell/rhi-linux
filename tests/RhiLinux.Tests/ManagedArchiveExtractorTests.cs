using System.IO.Compression;
using System.Text;
using RhiLinux.Core;
using RhiLinux.Mods;
using System.Diagnostics;

namespace RhiLinux.Tests;

public sealed class ManagedArchiveExtractorTests
{
    [Fact]
    public async Task ExtractsReShadeInstallerZipWithoutExternalTools()
    {
        using var temp = new TestDirectory();
        var installer = CreateReShadeInstallerZip(temp, "ReShade64.dll");
        var output = temp.Combine("out/ReShade64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await ManagedArchiveExtractor.ExtractSingleNamedFileAsync(
            installer, ArtifactFormat.ReShadeInstallerZip, "ReShade64.dll", output);
        Assert.True(File.Exists(output));
        Assert.Equal(PeArchitecture.X64, ArtifactValidator.ValidatePe(output, PeArchitecture.X64));
        Assert.Contains("Searching for add-ons", File.ReadAllText(output, Encoding.Latin1));
    }

    [Fact]
    public async Task ExtractsOfficialStyleReShadePeOverlayInstallerWithoutExternalTools()
    {
        using var temp = new TestDirectory();
        var installer = CreateReShadeInstallerPeOverlay(temp, "ReShade64.dll");
        Assert.Equal(ArtifactFormat.ReShadeInstallerZip,
            ManagedArchiveExtractor.DetectFormat(installer, ArtifactArchiveKind.ReShadeInstaller));
        var output = temp.Combine("out-pe/ReShade64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var cleared = ClearExtractorPath();
        await ManagedArchiveExtractor.ExtractSingleNamedFileAsync(
            installer, ArtifactFormat.ReShadeInstallerZip, "ReShade64.dll", output);
        Assert.True(File.Exists(output));
        Assert.Equal(PeArchitecture.X64, ArtifactValidator.ValidatePe(output, PeArchitecture.X64));
    }

    [Fact]
    public async Task ExtractsOptiScalerSevenZipWithoutExternalTools()
    {
        using var temp = new TestDirectory();
        var archive = CreateOptiScalerSevenZip(temp);
        var destination = temp.Directory("extracted");
        using var cleared = ClearExtractorPath();
        await ManagedArchiveExtractor.ExtractAsync(archive, destination, ArtifactFormat.SevenZipContainer, null);
        Assert.True(File.Exists(Path.Combine(destination, "OptiScaler.dll")));
        Assert.True(File.Exists(Path.Combine(destination, "OptiScaler.ini")));
        Assert.True(File.Exists(Path.Combine(destination, "setup_linux.sh")));
    }

    [Fact]
    public async Task ExtractsOptiScalerZipWithoutExternalTools()
    {
        using var temp = new TestDirectory();
        var archive = CreateOptiScalerZip(temp);
        var destination = temp.Directory("extracted-zip");
        await ManagedArchiveExtractor.ExtractAsync(archive, destination, ArtifactFormat.ZipContainer, null);
        Assert.True(File.Exists(Path.Combine(destination, "OptiScaler.dll")));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/tmp/escape.dll")]
    [InlineData("C:/Windows/escape.dll")]
    public void RejectsUnsafeArchivePaths(string unsafePath)
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            var normalized = ManagedArchiveExtractor.NormalizeArchivePath(unsafePath);
            ManagedArchiveExtractor.ValidateRelativePath(normalized);
        });
    }

    [Fact]
    public async Task RejectsZipTraversalEntries()
    {
        using var temp = new TestDirectory();
        var archive = temp.Combine("evil.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.txt");
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("nope"));
        }
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ManagedArchiveExtractor.ExtractAsync(archive, temp.Directory("out"), ArtifactFormat.ZipContainer, null));
    }

    [Fact]
    public async Task AcquireReShadeAndOptiScalerWithoutBsdtar()
    {
        using var temp = new TestDirectory();
        var paths = Paths(temp);
        var reshadeZip = CreateReShadeInstallerPeOverlay(temp, "ReShade64.dll");
        var optiArchive = CreateOptiScalerSevenZip(temp);
        var peAddon = temp.Pe("payload/renodx-fixture.addon64");
        var handler = new FixtureHandler(reshadeZip, optiArchive, peAddon);
        using var cleared = ClearExtractorPath();
        using var http = new HttpClient(handler);
        var cache = new ArtifactCacheService(paths);

        var reshade = await cache.AcquireAsync(new ArtifactSelection(
            ComponentKind.ReShade, "6.7.3",
            new Uri("https://reshade.me/downloads/ReShade_Setup_6.7.3_Addon.exe"),
            "6.7.3", "ReShade_Setup_6.7.3_Addon.exe", PeArchitecture.X64, null, "ReShade64.dll", null,
            ArtifactArchiveKind.ReShadeInstaller), http);
        Assert.Equal(ArtifactCacheState.Cached, reshade.CacheState);

        var opti = await cache.AcquireAsync(new ArtifactSelection(
            ComponentKind.OptiScaler, "v0.9.4",
            new Uri("https://github.com/optiscaler/OptiScaler/releases/download/v0.9.4/OptiScaler.7z"),
            "v0.9.4", "OptiScaler.7z", PeArchitecture.X64, null, "OptiScaler.dll", null,
            ArtifactArchiveKind.SevenZip), http);
        Assert.Equal(ArtifactCacheState.Cached, opti.CacheState);

        var reno = await cache.AcquireAsync(new ArtifactSelection(
            ComponentKind.RenoDx, "snapshot",
            new Uri("https://example.github.io/renodx/renodx-fixture.addon64"),
            "snapshot", "renodx-fixture.addon64", PeArchitecture.X64, null, "renodx-fixture.addon64", null,
            ArtifactArchiveKind.None, SourceValidatedByOfficialMetadata: true), http);
        Assert.Equal(ArtifactCacheState.Cached, reno.CacheState);
    }

    [Fact]
    public async Task ReShadePlanDoesNotRequireRenoDxProfile()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, "Unknown Adventure");
        var paths = Paths(temp);
        var reshadeZip = CreateReShadeInstallerZip(temp, "ReShade64.dll");
        var handler = new FixtureHandler(reshadeZip, CreateOptiScalerSevenZip(temp), temp.Pe("payload/x.addon64"));
        using var http = new HttpClient(handler);
        var resolver = new OfficialArtifactResolver(http, paths);
        var resolution = await resolver.ResolveAsync(game, allowNetwork: true);
        Assert.Contains(resolution.Artifacts, item => item.Component == ComponentKind.ReShade);
        Assert.DoesNotContain(resolution.Warnings, item =>
            item.Contains("No supported game profile", StringComparison.OrdinalIgnoreCase));

        resolution = await resolver.AcquireSelectedAsync(resolution,
            new HashSet<ComponentKind> { ComponentKind.ReShade }, allowNetwork: true);
        Assert.True(resolution.IsFullyAutomatic);
        Assert.Contains(resolution.Artifacts, item =>
            item.Component == ComponentKind.ReShade && item.CacheState == ArtifactCacheState.Cached);
        Assert.DoesNotContain(resolution.Warnings, item =>
            item.Contains("bsdtar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CapabilityPreflightReportsManagedSupportWithoutExternalTools()
    {
        using var temp = new TestDirectory();
        using var cleared = ClearExtractorPath();
        var report = ManagedArchiveExtractor.ProbeCapabilities(temp.Directory("cache"), temp.Directory("tmp"));
        Assert.True(report.ManagedZipSupported);
        Assert.True(report.ManagedReShadeInstallerSupported);
        Assert.True(report.ManagedSevenZipSupported);
        Assert.True(report.RequiredCapabilitiesAvailable);
        Assert.False(report.ExternalBsdtarPresent);
    }

    private static string CreateReShadeInstallerZip(TestDirectory temp, string dllName)
    {
        var dll = StageReShadeDll(temp, dllName);
        var installer = temp.Combine("ReShade_Setup_6.7.3_Addon.exe");
        using var zip = ZipFile.Open(installer, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(dll, dllName, CompressionLevel.NoCompression);
        return installer;
    }

    private static string CreateReShadeInstallerPeOverlay(TestDirectory temp, string dllName)
    {
        var dll = StageReShadeDll(temp, dllName);
        var zipPath = temp.Combine($"stage/{dllName}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(dll, dllName, CompressionLevel.NoCompression);
        var zipBytes = File.ReadAllBytes(zipPath);
        const int overlay = 0x400;
        var peOffset = 0x40;
        var optionalHeaderSize = 0x60;
        var sectionTable = peOffset + 4 + 20 + optionalHeaderSize;
        var buffer = new byte[overlay + zipBytes.Length];
        buffer[0] = 0x4D;
        buffer[1] = 0x5A;
        BitConverter.TryWriteBytes(buffer.AsSpan(0x3C), peOffset);
        BitConverter.TryWriteBytes(buffer.AsSpan(peOffset), 0x00004550);
        BitConverter.TryWriteBytes(buffer.AsSpan(peOffset + 4), (ushort)0x014C);
        BitConverter.TryWriteBytes(buffer.AsSpan(peOffset + 6), (ushort)1);
        BitConverter.TryWriteBytes(buffer.AsSpan(peOffset + 20), (ushort)optionalHeaderSize);
        Encoding.ASCII.GetBytes(".text").CopyTo(buffer.AsSpan(sectionTable));
        BitConverter.TryWriteBytes(buffer.AsSpan(sectionTable + 16), (uint)0x200);
        BitConverter.TryWriteBytes(buffer.AsSpan(sectionTable + 20), (uint)0x200);
        zipBytes.CopyTo(buffer.AsSpan(overlay));
        var installer = temp.Combine("ReShade_Setup_6.7.3_Addon_PE.exe");
        File.WriteAllBytes(installer, buffer);
        return installer;
    }

    private static string StageReShadeDll(TestDirectory temp, string dllName)
    {
        var dll = temp.Pe($"stage/{dllName}", size: 16 * 1024);
        using var stream = new FileStream(dll, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Position = 0x100;
        stream.Write(Encoding.ASCII.GetBytes("Searching for add-ons"));
        return dll;
    }

    private static string CreateOptiScalerZip(TestDirectory temp)
    {
        var root = temp.Directory("opti-src");
        WriteOptiScalerLayout(temp, root);
        var archive = temp.Combine("OptiScaler.zip");
        if (File.Exists(archive)) File.Delete(archive);
        ZipFile.CreateFromDirectory(root, archive);
        return archive;
    }

    private static string CreateOptiScalerSevenZip(TestDirectory temp)
    {
        var root = temp.Directory("opti-7z-src");
        WriteOptiScalerLayout(temp, root);
        var archive = temp.Combine("OptiScaler.7z");
        if (File.Exists(archive)) File.Delete(archive);
        var sevenZip = FindTool("7z") ?? FindTool("7za")
            ?? throw new InvalidOperationException("Creating the OptiScaler 7z fixture requires 7z/7za once at test authoring time.");
        var start = new ProcessStartInfo(sevenZip)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("a");
        start.ArgumentList.Add("-t7z");
        start.ArgumentList.Add("-mx=1");
        start.ArgumentList.Add(archive);
        start.ArgumentList.Add(".");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start 7z.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        return archive;
    }

    private static string? FindTool(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                 .Concat(["/run/current-system/sw/bin", "/usr/bin", "/bin"]))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void WriteOptiScalerLayout(TestDirectory temp, string root)
    {
        File.Copy(temp.PeWithMarker("pe-source/OptiScaler.dll", "OptiScaler"), Path.Combine(root, "OptiScaler.dll"), true);
        using (var stream = new FileStream(Path.Combine(root, "OptiScaler.dll"), FileMode.Open, FileAccess.ReadWrite))
            stream.SetLength(16 * 1024);
        File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n");
        File.WriteAllText(Path.Combine(root, "setup_linux.sh"),
            "proxies='dxgi.dll winmm.dll version.dll dbghelp.dll d3d12.dll wininet.dll winhttp.dll'\n");
        File.WriteAllText(Path.Combine(root, "setup_windows.bat"),
            "rem dxgi.dll winmm.dll version.dll dbghelp.dll d3d12.dll wininet.dll winhttp.dll\n");
        foreach (var name in new[]
                 {
                     "fakenvapi.dll", "fakenvapi.ini",
                     "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll",
                     "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll",
                     "D3D12_Optiscaler/D3D12Core.dll"
                 })
        {
            var destination = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(destination, "[fakenvapi]\n");
            else
            {
                File.Copy(temp.Pe($"pe-source/{name.Replace('/', '_')}", size: 8192), destination, true);
            }
        }
    }

    private static SteamGame Game(TestDirectory temp, string name)
    {
        var root = temp.Directory("game");
        var exe = temp.Pe("game/Game.exe");
        return new SteamGame(424242, name, temp.Path, temp.Path, root, temp.Combine("pfx"),
            exe, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(exe, 80, DetectionConfidence.High, PeArchitecture.X64, 8_000_000, ["fixture"])]);
    }

    private static XdgPaths Paths(TestDirectory temp) => new(temp.Path, new Dictionary<string, string?>
    {
        ["XDG_CACHE_HOME"] = temp.Combine("cache"),
        ["XDG_CONFIG_HOME"] = temp.Combine("config"),
        ["XDG_DATA_HOME"] = temp.Combine("data")
    });

    private static PathClearance ClearExtractorPath()
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "/var/empty-rhi-linux-path");
        return new PathClearance(original);
    }

    private sealed class PathClearance(string? original) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("PATH", original);
    }

    private sealed class FixtureHandler(string reshadePath, string optiPath, string addonPath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.Contains("ReShade_Setup", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(FileResponse(reshadePath));
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("<a href=\"/downloads/ReShade_Setup_6.7.3_Addon.exe\">full add-on support</a>")
                });
            }
            if (uri.AbsolutePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(FileResponse(optiPath));
            if (uri.AbsolutePath.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(FileResponse(addonPath));
            if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"version\":1}")
                });
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"tag_name\":\"v0.9.4\",\"html_url\":\"https://github.com/optiscaler/OptiScaler/releases/tag/v0.9.4\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[{\"name\":\"OptiScaler.7z\",\"browser_download_url\":\"https://github.com/optiscaler/OptiScaler/releases/download/v0.9.4/OptiScaler.7z\",\"size\":42}]}")
                });
            if (uri == RenoDxWikiClient.SourceUri)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        # List
                        | Name | Maintainer | Links | Status |
                        | --- | --- | --- | --- |
                        | Grim Dawn | x | [![Snapshot](https://img.shields.io/badge/x)](https://oopydoopy.github.io/renodx/renodx-grimdawn.addon64) | :white_check_mark: |
                        ## Multi-Game Mods
                        ### Unreal Engine [![Snapshot](https://img.shields.io/badge/x)](https://clshortfuse.github.io/renodx/renodx-unrealengine.addon64)
                        ### Unity Engine
                        64-bit:[![Snapshot](https://img.shields.io/badge/x)](https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64)
                        """)
                });
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage FileResponse(string path) =>
            new(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(File.ReadAllBytes(path))
            };
    }
}
