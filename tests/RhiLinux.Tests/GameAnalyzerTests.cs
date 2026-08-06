using RhiLinux.Core;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class GameAnalyzerTests
{
    [Fact]
    public async Task SinglePassCollectsEngineExecutablesAndAntiCheat()
    {
        using var temp = new TestDirectory();
        var shipping = temp.Pe("Project/Binaries/Win64/Example-Win64-Shipping.exe", size: 120 * 1024 * 1024);
        temp.Pe("launcher.exe", size: 30 * 1024 * 1024);
        temp.Directory("EasyAntiCheat");
        temp.File("d3d12.dll", "marker");

        var result = await new GameAnalyzer().AnalyzeAsync(
            new GameInstall("steam", "1", "Example", temp.Path),
            new GameAnalysisOptions());

        Assert.Equal(GameEngine.Unreal, result.Fingerprint.Engine);
        Assert.Equal(shipping, result.PrimaryExecutable?.Path);
        Assert.Equal(AntiCheatKind.EasyAntiCheat, result.Fingerprint.AntiCheat.Kind);
        Assert.Contains(GraphicsApiKind.Direct3D12, result.Fingerprint.GraphicsApis.Apis);
        Assert.True(result.Fingerprint.FilesVisited > 0);
    }

    [Fact]
    public async Task IndexReusesUnchangedGameAnalysis()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        temp.Directory("steam", "steamapps");
        temp.File("steam/steamapps/appmanifest_42.acf",
            """ "AppState" { "appid" "42" "name" "Example" "installdir" "Example" "StateFlags" "4" } """);
        temp.Pe("steam/steamapps/common/Example/Example.exe", size: 10 * 1024 * 1024);

        using var indexDir = new TestDirectory();
        var store = new JsonLibraryIndexStore(Path.Combine(indexDir.Path, "library-index.json"));
        var service = new SteamDiscoveryService(new ExecutableDetector(), libraryIndexStore: store);
        var options = new ScanOptions(LibraryIndexStore: store);

        var first = await service.ScanAsync([steam], null, CancellationToken.None, false, options);
        Assert.Single(first.Games);
        Assert.NotNull(service.LastMetrics);
        Assert.Equal(1, service.LastMetrics!.Analyzed);

        var second = await service.ScanAsync([steam], null, CancellationToken.None, false, options);
        Assert.Single(second.Games);
        Assert.Equal(1, service.LastMetrics!.CacheHits);
        Assert.Equal(0, service.LastMetrics.Analyzed);
    }
}

public sealed class LaunchCommandComposerTests
{
    [Fact]
    public void ComposesManagedFragmentsWithoutDroppingCommandPlaceholder()
    {
        var composition = LaunchCommandComposer.Compose(
            "SOME_USER_FLAG=1 %command%",
            "WINEDLLOVERRIDES=\"dxgi=n,b\"",
            enableGameMode: true,
            enableHdr: true);
        Assert.Contains("WINEDLLOVERRIDES=", composition.Text, StringComparison.Ordinal);
        Assert.Contains("gamemoderun", composition.Text, StringComparison.Ordinal);
        Assert.Contains("PROTON_ENABLE_WAYLAND=1", composition.Text, StringComparison.Ordinal);
        Assert.Contains("%command%", composition.Text, StringComparison.Ordinal);
    }
}

public sealed class PlatformCapabilityTests
{
    [Fact]
    public void DetectsWithoutThrowing()
    {
        var capabilities = new LinuxPlatformCapabilityProvider().Detect();
        Assert.False(string.IsNullOrWhiteSpace(capabilities.SessionType));
        Assert.False(string.IsNullOrWhiteSpace(capabilities.GpuVendor));
    }
}
