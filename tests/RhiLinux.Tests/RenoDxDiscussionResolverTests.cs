using System.Net;
using System.Text;
using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class RenoDxDiscussionResolverTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "renodx");

    [Fact]
    public void ParserAcceptsTrustedAttachmentAndRejectsCommenterAttachment()
    {
        var html = File.ReadAllText(Path.Combine(FixtureRoot, "discussion-with-addon.html"));
        var parsed = RenoDxDiscussionArtifactResolver.ParseDiscussionHtml(
            html, new Uri("https://github.com/clshortfuse/renodx/discussions/535"), "clshortfuse", "renodx");

        Assert.Equal("renodx-crimsondesert.addon64", parsed.ExpectedFileName);
        Assert.Equal("bin64", parsed.DeploymentRelativeDirectory);
        Assert.Contains(parsed.CompatibilityWarnings, item => item.Contains("injectors", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parsed.CompatibilityWarnings, item => item.Contains("EXE", StringComparison.OrdinalIgnoreCase));
        var candidate = Assert.Single(parsed.Candidates);
        Assert.Equal("renodx-crimsondesert.addon64", candidate.FileName);
        Assert.Contains("/user-attachments/files/999001/", candidate.CanonicalUrl.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("original-post", candidate.TrustReason);
    }

    [Fact]
    public void ParserKeepsManualOnlyWhenFilenameIsOnlyProse()
    {
        var html = File.ReadAllText(Path.Combine(FixtureRoot, "discussion-manual-only.html"));
        var parsed = RenoDxDiscussionArtifactResolver.ParseDiscussionHtml(
            html, new Uri("https://github.com/clshortfuse/renodx/discussions/535"), "clshortfuse", "renodx");
        Assert.Equal("renodx-crimsondesert.addon64", parsed.ExpectedFileName);
        Assert.Equal("bin64", parsed.DeploymentRelativeDirectory);
        Assert.Empty(parsed.Candidates);
    }

    [Fact]
    public void ParserAcceptsMaintainerPagesLink()
    {
        var html = File.ReadAllText(Path.Combine(FixtureRoot, "discussion-maintainer.html"));
        var parsed = RenoDxDiscussionArtifactResolver.ParseDiscussionHtml(
            html, new Uri("https://github.com/clshortfuse/renodx/discussions/100"), "clshortfuse", "renodx");
        var candidate = Assert.Single(parsed.Candidates);
        Assert.Equal("renodx-sample.addon64", candidate.FileName);
        Assert.Equal("repository-authority", candidate.TrustReason);
    }

    [Fact]
    public async Task SameAttachmentWithoutWikiTrustChainIsRejected()
    {
        var entry = ManualEntry("Example Game", discussionNumber: 10) with { OfficialCatalogOrigin = false };
        using var temp = new TestDirectory();
        var paths = Paths(temp);
        var resolver = new RenoDxDiscussionArtifactResolver(new HttpClient(new DiscussionHandler(temp)), paths);
        var result = await resolver.ResolveAsync(
            entry.DiscussionUrl!, entry, PeArchitecture.X64, allowNetwork: true);
        Assert.Equal(RenoDxDiscussionResolveState.NotTrustedSource, result.State);
        Assert.Null(result.Selection);
    }

    [Fact]
    public async Task CrimsonDesertFixtureResolvesInstallableDiscussionAddon()
    {
        RenoDxSnapshotReleaseResolver.ClearMemoryCacheForTests();
        using var temp = new TestDirectory();
        var pe = File.ReadAllBytes(temp.Pe("payload/renodx-crimsondesert.addon64"));
        var game = Game(temp, 3321460, "Crimson Desert", "bin64");
        var paths = Paths(temp);
        var wiki = WikiWithDiscussion("Crimson Desert", 535);
        SeedWiki(paths, wiki);
        var handler = new DiscussionHandler(temp)
        {
            WikiMarkdown = wiki,
            DiscussionHtml = File.ReadAllText(Path.Combine(FixtureRoot, "discussion-with-addon.html")),
            AddonPayloads =
            {
                ["https://github.com/user-attachments/files/999001/renodx-crimsondesert.addon64"] = pe
            }
        };
        using var http = new HttpClient(handler);
        var resolution = await new OfficialArtifactResolver(http, paths).ResolveAsync(game, allowNetwork: true);
        var report = await new StackStatusService(http, paths).GetAsync(game, allowNetwork: true);

        Assert.True(resolution.CanAcquireRenoDx);
        Assert.Equal(RenoDxCompatibilityState.ExactAddonAvailableFromOfficialDiscussion, resolution.RenoDxCompatibility);
        Assert.Equal("renodx-crimsondesert.addon64",
            Assert.Single(resolution.Artifacts, item => item.Component == ComponentKind.RenoDx).DeployFileName);
        Assert.Equal("bin64", resolution.RecommendedDeploymentRelativeDirectory);
        Assert.True(resolution.CanOpenOfficialPage);
        Assert.Contains(resolution.CompatibilityNotes, item => item.Contains("injectors", StringComparison.OrdinalIgnoreCase));
        var reno = Assert.Single(report.Components, item => item.Component == ComponentKind.RenoDx);
        Assert.Equal(ComponentHealth.DownloadRequired, reno.Health);
        Assert.Contains("Discussion", reno.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.CanInstallRecommendedStack || resolution.CanAcquireRenoDx);

        var renoSelection = Assert.Single(resolution.Artifacts, item => item.Component == ComponentKind.RenoDx);
        var acquiredReno = await new ArtifactCacheService(paths).AcquireAsync(renoSelection, http);
        Assert.Equal(ArtifactCacheState.Cached, acquiredReno.CacheState);
        Assert.Equal(ArtifactValidationState.Valid, acquiredReno.Validation);
        Assert.False(string.IsNullOrWhiteSpace(acquiredReno.Sha256));

        var card = new ComponentCardViewModel(reno,
            resolution.Components.Single(x => x.Component == ComponentKind.RenoDx) with { Selection = acquiredReno },
            officialPageUrl: resolution.OfficialPageUrl, compatibilityNotes: resolution.CompatibilityNotes);
        Assert.Equal("Exact addon available", card.State);
        Assert.True(card.CanInstall);
        Assert.True(card.CanOpenOfficialPage);
        Assert.Equal("Official RenoDX Discussion", card.SourceProfile);
    }

    [Fact]
    public async Task ManualOnlyDiscussionWithoutSnapshotAssetsShowsOfficialPageWithoutInstall()
    {
        RenoDxSnapshotReleaseResolver.ClearMemoryCacheForTests();
        using var temp = new TestDirectory();
        var game = Game(temp, 42, "Unique Manual Only Title", "bin64");
        var paths = Paths(temp);
        var wiki = WikiWithDiscussion("Unique Manual Only Title", 535);
        SeedWiki(paths, wiki);
        var handler = new DiscussionHandler(temp)
        {
            DiscussionHtml = File.ReadAllText(Path.Combine(FixtureRoot, "discussion-manual-only.html"))
                .Replace("Crimson Desert", "Unique Manual Only Title", StringComparison.Ordinal)
                .Replace("renodx-crimsondesert.addon64", "renodx-uniquemanualonlytitle.addon64", StringComparison.OrdinalIgnoreCase),
            WikiMarkdown = wiki
        };
        using var http = new HttpClient(handler);
        var resolution = await new OfficialArtifactResolver(http, paths).ResolveAsync(game, allowNetwork: true);
        Assert.False(resolution.CanAcquireRenoDx);
        Assert.True(resolution.RenoDxCompatibility is RenoDxCompatibilityState.OfficialPageAvailableNoDirectAddon or
            RenoDxCompatibilityState.ListedManualDownloadRequired);
        Assert.True(resolution.CanOpenOfficialPage);
        Assert.DoesNotContain(resolution.Artifacts, item => item.Component == ComponentKind.RenoDx);

        var report = await new StackStatusService(http, paths).GetAsync(game, allowNetwork: true);
        var reno = Assert.Single(report.Components, item => item.Component == ComponentKind.RenoDx);
        Assert.Equal(ComponentHealth.Supported, reno.Health);
        var card = new ComponentCardViewModel(reno, null, officialPageUrl: resolution.OfficialPageUrl);
        Assert.False(card.CanInstall);
        Assert.True(card.CanOpenOfficialPage);
    }

    [Fact]
    public void WikiParsesDiscussionUrlFromNameCell()
    {
        var entries = RenoDxWikiClient.ParseCatalog(WikiWithDiscussion("Crimson Desert", 535));
        var entry = Assert.Single(entries, item => item.CanonicalName == "Crimson Desert");
        Assert.Equal(RenoDxCatalogSection.ManualOnly, entry.SourceSection);
        Assert.Equal(RenoDxSourceType.Discussion, entry.SourceType);
        Assert.Equal(new Uri("https://github.com/clshortfuse/renodx/discussions/535"), entry.DiscussionUrl);
        Assert.False(entry.DirectAutomaticDownloadAvailable);
    }

    [Fact]
    public void NoHardCodedCrimsonDesertProductionPaths()
    {
        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src")),
        };
        foreach (var file in Directory.EnumerateFiles(roots[0], "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("3321460", text, StringComparison.Ordinal);
            Assert.DoesNotContain("renodx-crimsondesert", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("discussions/535", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OfflineCachedDiscussionStillInstallable()
    {
        using var temp = new TestDirectory();
        var pe = File.ReadAllBytes(temp.Pe("payload/renodx-crimsondesert.addon64"));
        var game = Game(temp, 77, "Crimson Desert", "bin64");
        var paths = Paths(temp);
        SeedWiki(paths, WikiWithDiscussion("Crimson Desert", 535));
        var handler = new DiscussionHandler(temp)
        {
            DiscussionHtml = File.ReadAllText(Path.Combine(FixtureRoot, "discussion-with-addon.html")),
            AddonPayloads =
            {
                ["https://github.com/user-attachments/files/999001/renodx-crimsondesert.addon64"] = pe
            }
        };
        using var online = new HttpClient(handler);
        _ = await new OfficialArtifactResolver(online, paths).ResolveAsync(game, allowNetwork: true);

        handler.FailDiscussion = true;
        using var offline = new HttpClient(handler);
        var resolution = await new OfficialArtifactResolver(offline, paths).ResolveAsync(game, allowNetwork: false);
        Assert.True(resolution.CanAcquireRenoDx);
        Assert.Equal(RenoDxCompatibilityState.ExactAddonAvailableFromOfficialDiscussion, resolution.RenoDxCompatibility);
    }

    private static string WikiWithDiscussion(string name, int number) => $"""
        # List
        | Name | Maintainer | Links | Status |
        | --- | --- | --- | --- |
        | [{name}](https://github.com/clshortfuse/renodx/discussions/{number}) | Author | [![Nexus Mods](https://img.shields.io/badge/x)](https://www.nexusmods.com/example/mods/1) [![Discord](https://img.shields.io/badge/x)](https://discord.gg/example) | :white_check_mark: |
        | Grim Dawn | OopyDoopy | [![Snapshot](https://img.shields.io/badge/x)](https://oopydoopy.github.io/renodx/renodx-grimdawn.addon64) | :white_check_mark: |

        ## Multi-Game Mods
        ### Unreal Engine [![Snapshot](https://img.shields.io/badge/x)](https://clshortfuse.github.io/renodx/renodx-unrealengine.addon64)
        ### Unity Engine
        64-bit:[![Snapshot](https://img.shields.io/badge/x)](https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64)
        """;

    private static RenoDxCatalogEntry ManualEntry(string name, int discussionNumber) => new(
        name, RenoDxIdentity.NormalizeKey(name), [], null, null, null, null, null, PeArchitecture.X64,
        RenoDxWikiStatus.Working, RenoDxCatalogSection.ManualOnly, RenoDxSourceType.Discussion, false, null, null, true,
        null)
    {
        DiscussionUrl = new Uri($"https://github.com/clshortfuse/renodx/discussions/{discussionNumber}"),
        OfficialPageUrl = new Uri($"https://github.com/clshortfuse/renodx/discussions/{discussionNumber}"),
        ExpectedAddonFileName = "renodx-sample.addon64"
    };

    private static SteamGame Game(TestDirectory temp, uint appId, string name, string deployRelative)
    {
        var root = temp.Directory($"game-{appId}");
        var deploy = temp.Directory($"game-{appId}/{deployRelative}");
        var exe = temp.Pe($"game-{appId}/{deployRelative}/Game.exe");
        return new SteamGame(appId, name, temp.Path, temp.Path, root, temp.Combine($"pfx-{appId}"),
            exe, deploy, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(exe, 80, DetectionConfidence.High, PeArchitecture.X64, 8_000_000, ["fixture"])]);
    }

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

    private sealed class DiscussionHandler(TestDirectory temp) : HttpMessageHandler
    {
        public string DiscussionHtml { get; set; } = string.Empty;
        public string? WikiMarkdown { get; set; }
        public Dictionary<string, byte[]> AddonPayloads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailDiscussion { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri == RenoDxWikiClient.SourceUri)
                return Ok(WikiMarkdown ?? WikiWithDiscussion("Crimson Desert", 535));
            if (uri.AbsolutePath.Contains("/discussions/", StringComparison.OrdinalIgnoreCase))
            {
                if (FailDiscussion)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Ok(DiscussionHtml);
            }
            if (AddonPayloads.TryGetValue(uri.GetLeftPart(UriPartial.Path), out var payload) ||
                AddonPayloads.TryGetValue(uri.AbsoluteUri, out payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                });
            }
            if (uri.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method == HttpMethod.Get && uri.AbsolutePath.Contains("ReShade_Setup", StringComparison.Ordinal))
                {
                    var dll = File.ReadAllBytes(temp.Pe("upstream/ReShade64.dll"));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(dll)
                    });
                }
                return Ok("<a href=\"/downloads/ReShade_Setup_6.7.3_Addon.exe\">full add-on support</a>");
            }
            if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return Ok("{\"version\":1}");
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                return Ok("{\"tag_name\":\"v1\",\"html_url\":\"https://github.com/optiscaler/OptiScaler/releases/tag/v1\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[]}");
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            });
    }
}
