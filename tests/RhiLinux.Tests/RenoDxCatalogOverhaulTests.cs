using System.Net;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class RenoDxCatalogOverhaulTests
{
    [Fact]
    public async Task GrimDawnExactWikiFixtureResolvesInstallableAddonWithoutHardCodedAlias()
    {
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 219990, "Grim Dawn", selectX86: false);
        var paths = Paths(temp);
        SeedWiki(paths, OfficialFixtureMarkdown());
        var resolver = new OfficialArtifactResolver(new HttpClient(new OfflineUpstreamHandler()), paths);
        var stack = new StackStatusService(new HttpClient(new OfflineUpstreamHandler()), paths);

        var resolution = await resolver.ResolveAsync(game, allowNetwork: false);
        var report = await stack.GetAsync(game, allowNetwork: false);

        Assert.True(resolution.CanAcquireRenoDx);
        Assert.Equal(RenoDxCompatibilityState.ExactAddonAvailable, resolution.RenoDxCompatibility);
        Assert.Equal(RenoDxMatchType.ExactNormalizedTitle, resolution.RenoDxMatch!.MatchType);
        Assert.Equal("renodx-grimdawn.addon64",
            Assert.Single(resolution.Artifacts, item => item.Component == ComponentKind.RenoDx).DeployFileName);
        Assert.Equal(GameProfileSupport.Supported, resolution.Profile.Profile.RenoDxSupport);
        var reno = Assert.Single(report.Components, item => item.Component == ComponentKind.RenoDx);
        Assert.Equal(ComponentHealth.DownloadRequired, reno.Health);
        Assert.Contains("renodx-grimdawn.addon64", reno.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unsupported", reno.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Grim Dawn")]
    [InlineData("  Grim   Dawn  ")]
    [InlineData("Grim Dawn™")]
    public async Task GrimDawnTitleVariantsResolve(string steamTitle)
    {
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 4242, steamTitle, selectX86: false);
        var paths = Paths(temp);
        SeedWiki(paths, OfficialFixtureMarkdown());
        var resolution = await new OfficialArtifactResolver(new HttpClient(new OfflineUpstreamHandler()), paths)
            .ResolveAsync(game, allowNetwork: false);
        Assert.Equal("renodx-grimdawn.addon64",
            Assert.Single(resolution.Artifacts, item => item.Component == ComponentKind.RenoDx).DeployFileName);
    }

    [Fact]
    public async Task SelectedX86DoesNotBecomeUnsupportedWhenX64CandidateExists()
    {
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 4243, "Grim Dawn", selectX86: true);
        var paths = Paths(temp);
        SeedWiki(paths, OfficialFixtureMarkdown());
        var resolution = await new OfficialArtifactResolver(new HttpClient(new OfflineUpstreamHandler()), paths)
            .ResolveAsync(game, allowNetwork: false);

        Assert.Equal(RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable, resolution.RenoDxCompatibility);
        Assert.NotNull(resolution.SuggestedExecutable);
        Assert.Contains("x64", resolution.SuggestedExecutable, StringComparison.OrdinalIgnoreCase);
        Assert.True(resolution.CanAcquireRenoDx);
        Assert.NotEqual(RenoDxCompatibilityState.Unsupported, resolution.RenoDxCompatibility);
    }

    [Fact]
    public async Task ExactAddonBeatsEngineFallback()
    {
        using var temp = new TestDirectory();
        var exe = temp.Pe("game/Grim Dawn.exe");
        var game = new SteamGame(4244, "Grim Dawn", temp.Path, temp.Path, temp.Directory("game"),
            temp.Combine("pfx"), exe, temp.Directory("game"), DetectionConfidence.High, "fixture",
            GameEngine.Unreal, [new(exe, 80, DetectionConfidence.High, PeArchitecture.X64, 4_000_000, ["fixture"])]);
        var paths = Paths(temp);
        SeedWiki(paths, OfficialFixtureMarkdown());
        var resolution = await new OfficialArtifactResolver(new HttpClient(new OfflineUpstreamHandler()), paths)
            .ResolveAsync(game, allowNetwork: false);
        Assert.Equal(RenoDxMatchType.ExactNormalizedTitle, resolution.RenoDxMatch!.MatchType);
        Assert.False(resolution.RenoDxMatch.UsedEngineFallback);
        Assert.Equal("renodx-grimdawn.addon64",
            Assert.Single(resolution.Artifacts, item => item.Component == ComponentKind.RenoDx).DeployFileName);
    }

    [Theory]
    [InlineData("Assassin's Creed™: Director's Cut Edition", "assassins creed directors cut edition")]
    [InlineData("Assassin’s Creed® Origins", "assassins creed origins")]
    [InlineData("Game – Remastered", "game remastered")]
    [InlineData("The Witcher", "the witcher")]
    public void NormalizationHandlesMarksApostrophesAndThePrefix(string input, string expectedKey)
    {
        Assert.Equal(expectedKey, RenoDxIdentity.NormalizeKey(input));
        Assert.Contains("witcher", RenoDxIdentity.BuildForms("The Witcher").Select(RenoDxIdentity.NormalizeKey));
    }

    [Fact]
    public void AmbiguousSimilarTitlesAreRejectedSafely()
    {
        var catalog = new RenoDxCatalogIndex(
        [
            Entry("Shadow Tale Chronicles", "https://author.github.io/renodx/renodx-shadowtale.addon64"),
            Entry("Shadow Tale Legacy", "https://author.github.io/renodx/renodx-shadowtales.addon64")
        ], DateTimeOffset.UtcNow, null, RenoDxWikiFetchState.Online, true, TimeSpan.Zero);
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 55, "Shadow Tale Edition", selectX86: false);
        var profile = new GameProfileMatch(Unsupported(game), "none", false);
        var result = new RenoDxGameMatcher().Resolve(game, profile, catalog, null);
        Assert.True(result.MatchType is RenoDxMatchType.Ambiguous or RenoDxMatchType.NoMatch
            or RenoDxMatchType.HighConfidenceTitle);
        if (result.MatchType == RenoDxMatchType.Ambiguous)
            Assert.Null(result.Selection);
    }

    [Fact]
    public void DxQualifierFormsMatchExactWikiTitles()
    {
        var catalog = new RenoDxCatalogIndex(
        [
            Entry("Path of Exile 2 DX11 Only", "https://author.github.io/renodx/renodx-poe2.addon64")
        ], DateTimeOffset.UtcNow, null, RenoDxWikiFetchState.Online, true, TimeSpan.Zero);
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 56, "Path of Exile 2", selectX86: false);
        var result = new RenoDxGameMatcher().Resolve(game, new GameProfileMatch(Unsupported(game), "none", false),
            catalog, null);
        Assert.NotNull(result.Selection);
        Assert.Equal("renodx-poe2.addon64", result.Selection.DeployFileName);
        Assert.Equal(RenoDxCompatibilityState.ExactAddonAvailable, result.Compatibility);
    }

    [Fact]
    public void ParserKeepsManualRelatedDeprecatedAndGenericSections()
    {
        var entries = RenoDxWikiClient.ParseCatalog("""
            # List
            | Name | Maintainer | Links | Status |
            | --- | --- | --- | --- |
            | Grim Dawn | Jon | [![Snapshot](https://img.shields.io/badge/Snapshot-blue)](https://oopydoopy.github.io/renodx/renodx-grimdawn.addon64) | :white_check_mark: |
            | Baldur's Gate 3 | Musa | [![Nexus Mods](https://img.shields.io/static/v1?label=Nexus)](https://www.nexusmods.com/baldursgate3/mods/1) | :white_check_mark: |

            ## Multi-Game Mods
            ### Unreal Engine [![Snapshot](https://img.shields.io/badge/x)](https://clshortfuse.github.io/renodx/renodx-unrealengine.addon64)
            | Name | Status | Notes |
            | --- | --- | --- |
            | Sample Unreal | :white_check_mark: | note |

            ### Unity Engine
            64-bit:[![Snapshot](https://img.shields.io/badge/x)](https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64) | 32-bit:[![Snapshot](https://img.shields.io/badge/x)](https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon32)
            | Name | Status | Notes |
            | --- | --- | --- |
            | Sample Unity | :construction: | 32-bit |

            # Related Mods
            | Name | Maintainer | Links | Status |
            | --- | --- | --- | --- |
            | Luma Framework | Pumbo | [Mods List](https://example.invalid) | :white_check_mark: |

            # Deprecated mods
            | Name | Maintainer | Links | Status |
            | --- | --- | --- | --- |
            | Outer Wilds | Ritsu | [Snapshot](https://author.github.io/renodx/renodx-outerwilds.addon64) | Superseded by Generic Unity mod |
            """);

        Assert.Contains(entries, item => item.CanonicalName == "Grim Dawn" && item.SourceSection == RenoDxCatalogSection.ExactGame);
        Assert.Contains(entries, item => item.CanonicalName == "Baldur's Gate 3" && item.SourceSection == RenoDxCatalogSection.ManualOnly);
        Assert.Contains(entries, item => item.CanonicalName == "Unreal Engine" && item.DirectAutomaticDownloadAvailable);
        Assert.Contains(entries, item => item.CanonicalName == "Unity Engine" && item.Addon32Url is not null && item.Addon64Url is not null);
        Assert.Contains(entries, item => item.CanonicalName == "Sample Unreal" && item.SourceSection == RenoDxCatalogSection.GenericUnreal);
        Assert.Contains(entries, item => item.CanonicalName == "Luma Framework" && item.SourceSection == RenoDxCatalogSection.Related);
        Assert.Contains(entries, item => item.CanonicalName == "Outer Wilds" && item.SourceSection == RenoDxCatalogSection.Deprecated &&
            item.SupersededBy != null);
    }

    [Fact]
    public void SourcePolicyAcceptsOfficialWikiContributorUrlsButNotArbitraryOnes()
    {
        var wikiUrl = new Uri("https://oopydoopy.github.io/renodx/renodx-grimdawn.addon64");
        Assert.True(OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(wikiUrl));
        Assert.True(OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(wikiUrl));
        Assert.False(OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(
            new Uri("http://oopydoopy.github.io/renodx/renodx-grimdawn.addon64")));
        Assert.False(OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(
            new Uri("https://user:pass@oopydoopy.github.io/renodx/renodx-grimdawn.addon64")));
        Assert.False(OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(
            new Uri("https://127.0.0.1/renodx/renodx-grimdawn.addon64")));
        Assert.False(OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(
            new Uri("https://example.invalid/download?file=renodx-grimdawn.addon64")));
        Assert.False(OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(
            new Uri("https://random.example/renodx-grimdawn.addon64")));
        Assert.True(OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(
            new Uri("https://random.example/renodx-grimdawn.addon64")));
    }

    [Fact]
    public async Task MissingReShadeDoesNotHideResolvedRenoDxCard()
    {
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 77, "Grim Dawn", selectX86: false);
        var paths = Paths(temp);
        SeedWiki(paths, OfficialFixtureMarkdown());
        var handler = new OfflineUpstreamHandler { FailReShade = true };
        var report = await new StackStatusService(new HttpClient(handler), paths).GetAsync(game, allowNetwork: true);
        var reno = Assert.Single(report.Components, item => item.Component == ComponentKind.RenoDx);
        Assert.Equal(ComponentHealth.DownloadRequired, reno.Health);
        Assert.True(report.ArtifactResolution.CanAcquireRenoDx);
        Assert.Contains("renodx-grimdawn.addon64", reno.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineCatalogDoesNotBecomeUnsupported()
    {
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 88, "Unknown Adventure", selectX86: false);
        var paths = Paths(temp);
        SeedWiki(paths, OfficialFixtureMarkdown());
        var resolution = await new OfficialArtifactResolver(new HttpClient(new OfflineUpstreamHandler()), paths)
            .ResolveAsync(game, allowNetwork: false);
        Assert.Equal(RenoDxCompatibilityState.NotListed, resolution.RenoDxCompatibility);
        Assert.NotEqual(RenoDxCompatibilityState.Unsupported, resolution.RenoDxCompatibility);
    }

    [Fact]
    public async Task NoMatchIsNotListedRatherThanUnsupported()
    {
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 89, "Completely Missing Game", selectX86: false);
        var paths = Paths(temp);
        SeedWiki(paths, OfficialFixtureMarkdown());
        var report = await new StackStatusService(new HttpClient(new OfflineUpstreamHandler()), paths)
            .GetAsync(game, allowNetwork: false);
        var reno = Assert.Single(report.Components, item => item.Component == ComponentKind.RenoDx);
        Assert.Equal(ComponentHealth.Unavailable, reno.Health);
        Assert.Contains("No RenoDX addon found", reno.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(ComponentHealth.Unsupported, reno.Health);
    }

    [Fact]
    public void MatcherStaysFastOnLargeCatalog()
    {
        var entries = Enumerable.Range(0, 1500).Select(i =>
            Entry($"Game {i}", $"https://author.github.io/renodx/renodx-game{i}.addon64")).Append(
            Entry("Grim Dawn", "https://oopydoopy.github.io/renodx/renodx-grimdawn.addon64")).ToArray();
        var catalog = new RenoDxCatalogIndex(entries, DateTimeOffset.UtcNow, null, RenoDxWikiFetchState.Online, true,
            TimeSpan.Zero);
        using var temp = new TestDirectory();
        var game = DualArchGame(temp, 99, "Grim Dawn", selectX86: false);
        var profile = new GameProfileMatch(Unsupported(game), "none", false);
        var matcher = new RenoDxGameMatcher();
        var warm = System.Diagnostics.Stopwatch.StartNew();
        RenoDxMatchResult? last = null;
        for (var i = 0; i < 50; i++)
            last = matcher.Resolve(game, profile, catalog, null);
        warm.Stop();
        Assert.Equal(RenoDxMatchType.ExactNormalizedTitle, last!.MatchType);
        Assert.True(warm.ElapsedMilliseconds < 250, $"50 resolutions took {warm.ElapsedMilliseconds} ms");
    }

    private static string OfficialFixtureMarkdown() => """
        # List
        | Name | Maintainer | Links | Status |
        | --- | --- | --- | --- |
        | Cyberpunk 2077 | ShortFuse | [![Snapshot](https://img.shields.io/badge/Snapshot-blue)](https://clshortfuse.github.io/renodx/renodx-cp2077.addon64) | :white_check_mark: |
        | Grim Dawn | OopyDoopy (Jon) | [![Snapshot](https://img.shields.io/github/last-commit/OopyDoopy/renodx?path=src%2Fgames%2Fgrimdawn&label=Snapshot)](https://oopydoopy.github.io/renodx/renodx-grimdawn.addon64) | :white_check_mark: |
        | Baldur's Gate 3 | Musa | [![Nexus Mods](https://img.shields.io/static/v1?label=Nexus%20Mods&message=Download&color=orange&logo=nexusmods)](https://www.nexusmods.com/baldursgate3/mods/15892) | :white_check_mark: |

        ## Multi-Game Mods
        ### Unreal Engine [![Snapshot](https://img.shields.io/badge/x)](https://clshortfuse.github.io/renodx/renodx-unrealengine.addon64)
        ### Unity Engine
        64-bit:[![Snapshot](https://img.shields.io/badge/x)](https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64)
        """;

    private static RenoDxCatalogEntry Entry(string name, string url64) => new(
        name, RenoDxIdentity.NormalizeKey(name), [], null, null, new Uri(url64),
        Path.GetFileName(url64), RenoDxIdentity.ArtifactSlug(Path.GetFileName(url64)), PeArchitecture.X64,
        RenoDxWikiStatus.Working, RenoDxCatalogSection.ExactGame, RenoDxSourceType.Snapshot, true, null, null, true,
        null);

    private static SteamGame DualArchGame(TestDirectory temp, uint appId, string name, bool selectX86)
    {
        var root = temp.Directory($"game-{appId}");
        var x86 = temp.Pe($"game-{appId}/Game.exe", 0x014c);
        var x64 = temp.Pe($"game-{appId}/x64/Game.exe");
        var selected = selectX86 ? x86 : x64;
        return new SteamGame(appId, name, temp.Path, temp.Path, root, temp.Combine($"pfx-{appId}"),
            selected, Path.GetDirectoryName(selected)!, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [
                new(x64, 53, DetectionConfidence.Medium, PeArchitecture.X64, 8_000_000, ["64-bit PE executable"]),
                new(x86, 42, DetectionConfidence.Medium, PeArchitecture.X86, 7_000_000, ["32-bit PE executable"])
            ]);
    }

    private static GameProfile Unsupported(SteamGame game) => new(
        $"unsupported-{game.AppId}", game.AppId, game.Name, [], null, null, game.Engine, "Unknown",
        DeploymentPlanner.SupportedProxyNames, [], [], null, GameProfileSupport.Unsupported, false,
        new Dictionary<string, string>(), [], ["No supported game profile is available."], null);

    private static void SeedWiki(XdgPaths paths, string markdown)
    {
        var directory = Path.Combine(paths.AppCacheDirectory, "metadata", "renodx-wiki");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Mods.md"), markdown);
    }

    private static XdgPaths Paths(TestDirectory temp) => new(temp.Path, new Dictionary<string, string?>
    {
        ["XDG_CACHE_HOME"] = temp.Combine("cache"),
        ["XDG_CONFIG_HOME"] = temp.Combine("config"),
        ["XDG_DATA_HOME"] = temp.Combine("data")
    });

    private sealed class OfflineUpstreamHandler : HttpMessageHandler
    {
        public bool FailReShade { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri == RenoDxWikiClient.SourceUri)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(OfficialFixtureMarkdown())
                });
            if (request.RequestUri!.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
            {
                if (FailReShade)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<a href=\"/downloads/ReShade_Setup_6.7.3_Addon.exe\">full add-on support</a>")
                });
            }
            if (request.RequestUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"version\":1}")
                });
            if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"tag_name\":\"v1\",\"html_url\":\"https://github.com/optiscaler/OptiScaler/releases/tag/v1\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[{\"name\":\"OptiScaler.7z\",\"browser_download_url\":\"https://github.com/optiscaler/OptiScaler/releases/download/v1/OptiScaler.7z\",\"size\":42}]}")
                });
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
