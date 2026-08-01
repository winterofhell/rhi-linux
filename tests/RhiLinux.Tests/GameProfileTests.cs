using RhiLinux.Core;
using RhiLinux.Mods;
using System.Text.Json;

namespace RhiLinux.Tests;

public sealed class GameProfileTests
{
    [Fact]
    public async Task MatchesCyberpunkPrimarilyBySteamAppId()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, 1091500, "A localized title", GameEngine.Unity);

        var match = await new GameProfileCatalog().MatchAsync(game);

        Assert.True(match.ExactAppId);
        Assert.Equal("steam-1091500-cyberpunk-2077", match.Profile.Id);
        Assert.Equal("renodx-cp2077.addon64", match.Profile.RenoDx!.FileName);
    }

    [Fact]
    public async Task DoesNotApplyCyberpunkProfileToWrongAppIdWithSimilarName()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, 123, "Cyberpunk 2077", GameEngine.Unknown);

        var match = await new GameProfileCatalog().MatchAsync(game);

        Assert.False(match.ExactAppId);
        Assert.NotEqual("steam-1091500-cyberpunk-2077", match.Profile.Id);
        Assert.Equal(GameProfileSupport.Unsupported, match.Profile.RenoDxSupport);
    }

    [Fact]
    public async Task NightSwarmUsesExplicitExperimentalUnityMapping()
    {
        using var temp = new TestDirectory();
        var match = await new GameProfileCatalog().MatchAsync(Game(temp, 3668370, "Night Swarm", GameEngine.Unity));

        Assert.True(match.ExactAppId);
        Assert.Equal(GameProfileSupport.EngineFallback, match.Profile.RenoDxSupport);
        Assert.False(match.Profile.RenoDx!.IsGameSpecific);
    }

    [Fact]
    public async Task UnknownUnityGameUsesArchitectureCorrectOfficialFallback()
    {
        using var temp = new TestDirectory();
        var x64 = await new GameProfileCatalog().MatchAsync(Game(temp, 700, "Unknown Unity", GameEngine.Unity));
        var x86 = await new GameProfileCatalog().MatchAsync(Game(temp, 701, "Unknown Unity 32", GameEngine.Unity, 0x014c));

        Assert.Equal(GameProfileSupport.EngineFallback, x64.Profile.RenoDxSupport);
        Assert.Equal("renodx-unityengine.addon64", x64.Profile.RenoDx!.FileName);
        Assert.Equal(PeArchitecture.X64, x64.Profile.RenoDx.Architecture);
        Assert.Equal("renodx-unityengine.addon32", x86.Profile.RenoDx!.FileName);
        Assert.Equal(PeArchitecture.X86, x86.Profile.RenoDx.Architecture);
    }

    [Fact]
    public async Task UnknownModernUnrealGameUsesOfficialFallback()
    {
        using var temp = new TestDirectory();
        var match = await new GameProfileCatalog().MatchAsync(Game(temp, 702, "Unknown Unreal", GameEngine.Unreal));

        Assert.Equal(GameProfileSupport.EngineFallback, match.Profile.RenoDxSupport);
        Assert.Equal("renodx-unrealengine.addon64", match.Profile.RenoDx!.FileName);
    }

    [Theory]
    [InlineData(GameEngine.Unknown)]
    [InlineData(GameEngine.ReEngine)]
    [InlineData(GameEngine.UnrealLegacy)]
    public async Task UnsupportedEnginesNeverReceiveAGenericAddon(GameEngine engine)
    {
        using var temp = new TestDirectory();
        var match = await new GameProfileCatalog().MatchAsync(Game(temp, 703, "Unsupported Fixture", engine));

        Assert.Equal(GameProfileSupport.Unsupported, match.Profile.RenoDxSupport);
        Assert.Null(match.Profile.RenoDx);
    }

    [Fact]
    public async Task AmbiguousExecutableBlocksEngineFallback()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("ambiguous");
        var executable = temp.Pe("ambiguous/Game.exe");
        var game = new SteamGame(704, "Ambiguous Unity", temp.Path, temp.Path, root, temp.Combine("pfx"), executable, root,
            DetectionConfidence.Low, "ambiguous", GameEngine.Unity, []);

        var match = await new GameProfileCatalog().MatchAsync(game);

        Assert.Equal(GameProfileSupport.Unsupported, match.Profile.RenoDxSupport);
        Assert.Contains("ambiguous", match.MatchReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewedExecutableAndAliasProfilesMatchWithoutGuessingByTitle()
    {
        using var temp = new TestDirectory();
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var catalog = new GameProfileCatalog(paths);
        var profile = new GameProfile("known-profile", null, "Known Game", ["Known Alias"], "KnownGame.exe", null,
            GameEngine.Unknown, "DirectX", ["dxgi.dll"], [], [],
            new(new Uri("https://clshortfuse.github.io/renodx/renodx-known.addon64"), "renodx-known.addon64", "snapshot", PeArchitecture.X64, true),
            GameProfileSupport.Supported, false, new Dictionary<string, string>(), [], [], null);
        var document = new GameProfileCatalog.GameProfileDocument { Profiles = [profile], UpdatedUtc = DateTimeOffset.UtcNow };
        await using (var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
            await catalog.UpdateCacheAsync(stream);
        var executableRoot = temp.Directory("known-executable");
        var executablePath = temp.Pe("known-executable/KnownGame.exe");
        var executableGame = new SteamGame(800, "Unrelated localized name", temp.Path, temp.Path, executableRoot, temp.Combine("pfx"),
            executablePath, executableRoot, DetectionConfidence.High, "fixture", GameEngine.Unknown, []);
        var aliasRoot = temp.Directory("known-alias");
        var aliasPath = temp.Pe("known-alias/Other.exe");
        var aliasGame = new SteamGame(801, "Known Alias", temp.Path, temp.Path, aliasRoot, temp.Combine("pfx2"),
            aliasPath, aliasRoot, DetectionConfidence.High, "fixture", GameEngine.Unknown, []);

        var executableMatch = await catalog.MatchAsync(executableGame);
        var aliasMatch = await catalog.MatchAsync(aliasGame);

        Assert.Equal("known-profile", executableMatch.Profile.Id);
        Assert.Contains("executable", executableMatch.MatchReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("known-profile", aliasMatch.Profile.Id);
        Assert.Contains("alias", aliasMatch.MatchReason, StringComparison.OrdinalIgnoreCase);
    }

    private static SteamGame Game(TestDirectory temp, uint appId, string name, GameEngine engine, ushort machine = 0x8664)
    {
        var root = temp.Directory($"game-{appId}"); var exe = temp.Pe($"game-{appId}/Game.exe", machine);
        return new(appId, name, temp.Path, temp.Path, root, temp.Combine("pfx"), exe, root,
            DetectionConfidence.High, "test", engine, []);
    }
}
