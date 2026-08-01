using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class ProxyDiagnosticsTests
{
    [Fact]
    public async Task CyberpunkDbgHelpIsGameOwnedAndAlternativeIsSelected()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, 1091500, "Cyberpunk 2077");
        temp.PeWithMarker("game/dbghelp.dll", "Microsoft Corporation\0Debugging Tools for Windows");

        var result = await new ProxyDiagnosticsService().DiagnoseAsync(game);

        var dbghelp = Assert.Single(result.Candidates, x => x.ProxyName == "dbghelp.dll");
        Assert.Equal(ProxyFileClassification.KnownGameOwnedDll, dbghelp.Classification);
        Assert.True(dbghelp.ProhibitedByProfile);
        Assert.True(dbghelp.ProtonOverrideAppropriate);
        Assert.Equal("version.dll", result.SelectedProxy);
        Assert.True(Assert.Single(result.Candidates, x => x.ProxyName == "version.dll").Score > dbghelp.Score);
    }

    [Fact]
    public async Task UnknownDbgHelpIsNotTreatedAsAComponentAndSafeAlternativeRemainsAvailable()
    {
        using var temp = new TestDirectory(); var game = Game(temp);
        temp.Pe("game/dbghelp.dll");

        var result = await new ProxyDiagnosticsService().DiagnoseAsync(game);

        Assert.Equal(ProxyFileClassification.UnknownDll, Assert.Single(result.Candidates, x => x.ProxyName == "dbghelp.dll").Classification);
        Assert.NotNull(result.SelectedProxy);
        Assert.NotEqual("dbghelp.dll", result.SelectedProxy);
    }

    [Theory]
    [InlineData("reshade.me", ProxyFileClassification.KnownThirdPartyInjector)]
    [InlineData("OptiScaler", ProxyFileClassification.KnownThirdPartyInjector)]
    public async Task BinaryMarkersIdentifyKnownUnmanagedInjectors(string marker, ProxyFileClassification expected)
    {
        using var temp = new TestDirectory(); var game = Game(temp);
        temp.PeWithMarker("game/dxgi.dll", marker);

        var result = await new ProxyDiagnosticsService().DiagnoseAsync(game);

        Assert.Equal(expected, Assert.Single(result.Candidates, x => x.ProxyName == "dxgi.dll").Classification);
    }

    [Fact]
    public async Task OwnershipManifestAndHashIdentifyManagedReShade()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var source = temp.PeWithMarker("source/ReShade64.dll", "reshade.me");
        var planner = new DeploymentPlanner();
        Assert.True((await new DeploymentExecutor().ExecuteAsync(
            await planner.BuildInstallPlanAsync(game, new(ComponentKind.ReShade, source, "ReShade64.dll")), false)).Succeeded);

        var result = await new ProxyDiagnosticsService().DiagnoseAsync(game);

        Assert.Equal(ProxyFileClassification.ManagedReShade, Assert.Single(result.Candidates, x => x.ProxyName == "dxgi.dll").Classification);
    }

    [Fact]
    public async Task OwnershipManifestAndHashIdentifyManagedOptiScaler()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var source = temp.PeWithMarker("source/OptiScaler.dll", "OptiScaler");
        var planner = new DeploymentPlanner();
        Assert.True((await new DeploymentExecutor().ExecuteAsync(
            await planner.BuildInstallPlanAsync(game,
                new(ComponentKind.OptiScaler, source, "OptiScaler.dll", "v0.9.4")), false)).Succeeded);

        var result = await new ProxyDiagnosticsService().DiagnoseAsync(game);

        Assert.Equal(ProxyFileClassification.ManagedOptiScaler, Assert.Single(result.Candidates, x => x.ProxyName == "dxgi.dll").Classification);
    }

    [Fact]
    public async Task RenoDxReportsMissingDependencyWhenNoSafeReShadeProxyExists()
    {
        using var temp = new TestDirectory(); var game = Game(temp, 3668370, "Night Swarm");
        foreach (var proxy in DeploymentPlanner.SupportedProxyNames) temp.Pe($"game/{proxy}");

        var statuses = await new ComponentDetector().DetectAsync(game);

        var reno = Assert.Single(statuses, x => x.Component == ComponentKind.RenoDx);
        Assert.Equal(ComponentHealth.MissingDependency, reno.Health);
        Assert.Contains("ReShade", reno.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipleOccupiedNamesSelectFirstSafeAlternative()
    {
        using var temp = new TestDirectory(); var game = Game(temp);
        temp.Pe("game/dxgi.dll"); temp.Pe("game/winmm.dll");

        var result = await new ProxyDiagnosticsService().DiagnoseAsync(game);

        Assert.Equal("d3d12.dll", result.SelectedProxy);
        Assert.True(result.Candidates.SequenceEqual(result.Candidates.OrderByDescending(x => x.Score)));
        Assert.Contains(result.RejectedAlternatives, x => x.StartsWith("dxgi.dll:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportsPreciseFailureWhenEveryProxyIsOccupied()
    {
        using var temp = new TestDirectory(); var game = Game(temp);
        foreach (var proxy in DeploymentPlanner.SupportedProxyNames) temp.Pe($"game/{proxy}");

        var result = await new ProxyDiagnosticsService().DiagnoseAsync(game);

        Assert.False(result.HasSafeProxy);
        Assert.Null(result.LaunchOption);
        Assert.All(result.Candidates, x => Assert.False(x.SafeForNewInstallation));
        Assert.Equal(DeploymentPlanner.SupportedProxyNames.Length, result.RejectedAlternatives.Count);
    }

    [Fact]
    public async Task PlannerConflictListsRequiredWriteAndEveryRejectedProxy()
    {
        using var temp = new TestDirectory(); var game = Game(temp);
        foreach (var proxy in DeploymentPlanner.SupportedProxyNames) temp.Pe($"game/{proxy}");
        var artifact = new ComponentArtifact(ComponentKind.ReShade,
            temp.PeWithMarker("stage/ReShade64.dll", "reshade.me"), "ReShade64.dll", "6.7.3");

        var exception = await Assert.ThrowsAsync<DeploymentConflictException>(() =>
            new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, new(artifact, null, null)));

        Assert.Contains("unknown file would need to be replaced", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Required write: ReShade", exception.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains("Every supported proxy alternative was rejected", exception.TechnicalDetails, StringComparison.Ordinal);
        foreach (var proxy in DeploymentPlanner.SupportedProxyNames)
            Assert.Contains(proxy, exception.TechnicalDetails, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangedManagedProxyIsReportedAsPartialInstallation()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var source = temp.Pe("source/ReShade64.dll");
        var executor = new DeploymentExecutor(); var planner = new DeploymentPlanner();
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, new(ComponentKind.ReShade, source, "ReShade64.dll")), false)).Succeeded);
        await File.AppendAllTextAsync(Path.Combine(game.GameRoot, "dxgi.dll"), "changed");

        var result = await new ProxyDiagnosticsService().DiagnoseAsync(game);

        Assert.Equal(ProxyFileClassification.BrokenOrPartialManagedInstallation,
            Assert.Single(result.Candidates, x => x.ProxyName == "dxgi.dll").Classification);
    }

    private static SteamGame Game(TestDirectory temp, uint appId = 900001, string name = "Fixture Game")
    {
        var root = temp.Directory("game"); var exe = temp.Pe("game/Game.exe");
        return new(appId, name, temp.Path, temp.Path, root, temp.Combine("compatdata", appId.ToString(), "pfx"), exe, root,
            DetectionConfidence.High, "test", GameEngine.Unknown, []);
    }
}
