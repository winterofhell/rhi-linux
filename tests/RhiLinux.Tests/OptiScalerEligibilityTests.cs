using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class OptiScalerEligibilityTests
{
    [Fact]
    public async Task UnknownX64ProtonGameIsExperimentalWithoutRenoDxProfile()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, PeArchitecture.X64);
        var catalog = new GameProfileCatalog();
        var profile = await catalog.MatchAsync(game);
        var proxy = await new ProxyDiagnosticsService(catalog).DiagnoseAsync(game);

        var eligibility = OptiScalerEligibilityService.Evaluate(game, profile, proxy);

        Assert.Equal(OptiScalerCompatibilityLevel.Experimental, eligibility.Level);
        Assert.True(eligibility.CanInstall);
        Assert.True(eligibility.RequiresExplicitConfirmation);
        Assert.Null(profile.Profile.RenoDx);
    }

    [Fact]
    public async Task UnsupportedArchitectureAndAntiCheatAreBlocked()
    {
        using var temp = new TestDirectory();
        var x86 = Game(temp, PeArchitecture.X86);
        var catalog = new GameProfileCatalog();
        var x86Eligibility = OptiScalerEligibilityService.Evaluate(x86, await catalog.MatchAsync(x86),
            await new ProxyDiagnosticsService(catalog).DiagnoseAsync(x86));
        Assert.Equal(OptiScalerCompatibilityLevel.Unsupported, x86Eligibility.Level);

        var antiCheat = x86 with { RequiresConfirmation = true };
        var blocked = OptiScalerEligibilityService.Evaluate(antiCheat, await catalog.MatchAsync(antiCheat),
            await new ProxyDiagnosticsService(catalog).DiagnoseAsync(antiCheat));
        Assert.Equal(OptiScalerCompatibilityLevel.BlockedByAntiCheat, blocked.Level);
    }

    [Fact]
    public void CurrentTemplateSchemaPreservesSettingsAndRejectsUnknownSchema()
    {
        using var temp = new TestDirectory();
        var template = temp.File("OptiScaler.ini", "[Plugins]\nPath=custom\nLoadAsiPlugins=auto\nLoadReshade=auto\n");
        var schema = OptiScalerConfigurationAdapter.Detect("v0.7.7", null, template, true, false);
        Assert.Equal("Plugins", schema.ReShadeSection);
        Assert.Collection(schema.Changes,
            change =>
            {
                Assert.Equal("LoadReshade", change.Key);
                Assert.Equal("auto", change.PreviousValue);
                Assert.Equal("true", change.ResultingValue);
            },
            change =>
            {
                Assert.Equal("LoadAsiPlugins", change.Key);
                Assert.Equal("auto", change.PreviousValue);
                Assert.Equal("false", change.ResultingValue);
            });
        Assert.Throws<NotSupportedException>(() =>
            OptiScalerConfigurationAdapter.Detect("2020-unknown", null, null, true, false));
        Assert.Throws<NotSupportedException>(() =>
            OptiScalerConfigurationAdapter.Detect("v1.2.3", null, null, true, false));
    }

    private static SteamGame Game(TestDirectory temp, PeArchitecture architecture)
    {
        var root = temp.Directory($"game-{architecture}");
        var machine = architecture == PeArchitecture.X86 ? (ushort)0x014c : (ushort)0x8664;
        var executable = temp.Pe($"game-{architecture}/Game.exe", machine);
        return new(987654, "Unknown Fixture", temp.Path, temp.Path, root, temp.Combine("compatdata", "987654", "pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, architecture, new FileInfo(executable).Length, ["fixture"])]);
    }
}
