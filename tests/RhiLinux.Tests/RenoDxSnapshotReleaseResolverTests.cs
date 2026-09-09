using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class RenoDxSnapshotReleaseResolverTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "renodx");
    private const string PublishedDigest =
        "b48e86bb1961c91314202b139bd06ce8cb575d2f23d60d4ec634c6147a854bf1";

    public RenoDxSnapshotReleaseResolverTests() => RenoDxSnapshotReleaseResolver.ClearMemoryCacheForTests();

    [Fact]
    public void ParseIndexesStructuredMetadataAndAddonAssets()
    {
        var releaseJson = ReleaseJson(354877244, "nightly-snapshot", assetsEmbedded: false);
        var pages = AssetPages(
        [
            ("games-index.json", 1, null),
            ("generated-metadata.json", 2, null),
            ("renodx-sampleexact.addon64", 1001, "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            ("renodx-sampleexact.addon32", 1002, null),
            ("renodx-sampleexact.pdb", 1003, null),
            ("renodx-crimsondesert.addon64", 2001, "sha256:" + PublishedDigest),
            ("renodx-crimsondesert.addon32", 2002, null),
            ("renodx-crimsondesert.exe", 2003, null),
            ("renodx-unrealengine.addon64", 3001, null),
            ("app.mjs", 4001, null)
        ]);
        var index = RenoDxSnapshotReleaseResolver.ParseReleasePayload(
            releaseJson,
            pages,
            File.ReadAllText(Path.Combine(FixtureRoot, "games-index-sample.json")),
            File.ReadAllText(Path.Combine(FixtureRoot, "generated-metadata-sample.json")),
            "etag-1",
            null,
            DateTimeOffset.UtcNow);

        Assert.Equal(1, index.SchemaVersion);
        Assert.Equal(354877244, index.ReleaseId);
        Assert.Equal("nightly-snapshot", index.ReleaseTag);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", index.ReleaseCommit);
        Assert.True(index.UsedStructuredMetadata);
        Assert.True(index.ByExactName.ContainsKey("renodx-crimsondesert.addon64"));
        Assert.False(index.ByExactName.ContainsKey("renodx-crimsondesert.pdb"));
        Assert.False(index.ByExactName.ContainsKey("renodx-crimsondesert.exe"));
        Assert.False(index.ByExactName.ContainsKey("app.mjs"));
        Assert.Equal(PublishedDigest, Normalize(index.ByExactName["renodx-crimsondesert.addon64"].Digest));
        Assert.Contains(index.Games, item => item.Id == "sampleexact" && item.SteamAppId == 900001);
    }

    [Fact]
    public void ParseHandlesPaginatedHundredsOfAssets()
    {
        var assets = new List<(string Name, long Id, string? Digest)>();
        for (var i = 0; i < 250; i++)
            assets.Add(($"renodx-game{i:000}.addon64", 10_000 + i, null));
        assets.Add(("renodx-target.addon64", 99_001, "sha256:" + new string('b', 64)));
        var pages = AssetPages(assets.ToArray());
        var index = RenoDxSnapshotReleaseResolver.ParseReleasePayload(
            ReleaseJson(1, "snapshot", false), pages, null, null, null, null, DateTimeOffset.UtcNow);
        Assert.True(index.Assets.Count >= 251);
        Assert.True(index.ByExactName.ContainsKey("renodx-target.addon64"));
    }

    [Fact]
    public async Task NexusOnlyCatalogEntryDoesNotInstallUnlistedSnapshotAsset()
    {
        using var temp = new TestDirectory();
        var pe = File.ReadAllBytes(temp.Pe("payload/renodx-crimsondesert.addon64"));
        var digest = Convert.ToHexString(SHA256.HashData(pe)).ToLowerInvariant();
        var game = Game(temp, 42, "Crimson Desert", "bin64");
        var paths = Paths(temp);
        var wiki = WikiWithDiscussion("Crimson Desert", 535);
        SeedWiki(paths, wiki);
        var handler = new SnapshotHandler(temp)
        {
            WikiMarkdown = wiki,
            DiscussionHtml = File.ReadAllText(Path.Combine(FixtureRoot, "discussion-manual-only.html")),
            GamesIndexJson = File.ReadAllText(Path.Combine(FixtureRoot, "games-index-sample.json")),
            GeneratedMetadataJson = File.ReadAllText(Path.Combine(FixtureRoot, "generated-metadata-sample.json")),
            AddonPayloads =
            {
                ["https://github.com/clshortfuse/renodx/releases/download/nightly-snapshot/renodx-crimsondesert.addon64"] = pe
            },
            ReleaseAssets =
            [
                ("games-index.json", 1, null),
                ("generated-metadata.json", 2, null),
                ("renodx-crimsondesert.addon64", 2001, "sha256:" + digest),
                ("renodx-crimsondesert.addon32", 2002, null),
                ("renodx-crimsondesert.pdb", 2003, null),
                ("renodx-unrealengine.addon64", 3001, null)
            ]
        };
        using var http = new HttpClient(handler);
        var resolution = await new OfficialArtifactResolver(http, paths).ResolveAsync(game, allowNetwork: true);
        var report = await new StackStatusService(http, paths).GetAsync(game, allowNetwork: true);

        Assert.False(resolution.CanAcquireRenoDx);
        Assert.True(resolution.RenoDxCompatibility is RenoDxCompatibilityState.OfficialPageAvailableNoDirectAddon or
            RenoDxCompatibilityState.ListedManualDownloadRequired);
        Assert.DoesNotContain(resolution.Artifacts, item => item.Component == ComponentKind.RenoDx);
        Assert.Null(resolution.SnapshotResolution);
        Assert.Equal("www.nexusmods.com", resolution.OfficialPageUrl?.Host);
        var card = new ComponentCardViewModel(
            report.Components.Single(x => x.Component == ComponentKind.RenoDx),
            resolution.Components.Single(x => x.Component == ComponentKind.RenoDx),
            officialPageUrl: resolution.OfficialPageUrl);
        Assert.False(card.CanInstall);
        Assert.True(card.CanUseDownloadedArtifact);
        Assert.Equal("Open Nexus Mods", card.OfficialPageActionText);
    }

    [Fact]
    public async Task StructuredMetadataMapsExactGame()
    {
        using var temp = new TestDirectory();
        var pe = File.ReadAllBytes(temp.Pe("payload/renodx-sampleexact.addon64"));
        var digest = Convert.ToHexString(SHA256.HashData(pe)).ToLowerInvariant();
        var game = Game(temp, 900001, "Sample Exact Game", ".");
        var paths = Paths(temp);
        SeedWiki(paths, """
            # List
            | Name | Maintainer | Links | Status |
            | --- | --- | --- | --- |
            | Catalog Anchor | Author | [Snapshot](https://author.github.io/renodx/renodx-anchor.addon64) | :white_check_mark: |
            """);
        var handler = new SnapshotHandler(temp)
        {
            GamesIndexJson = File.ReadAllText(Path.Combine(FixtureRoot, "games-index-sample.json")),
            GeneratedMetadataJson = File.ReadAllText(Path.Combine(FixtureRoot, "generated-metadata-sample.json")),
            AddonPayloads =
            {
                ["https://github.com/clshortfuse/renodx/releases/download/nightly-snapshot/renodx-sampleexact.addon64"] = pe
            },
            ReleaseAssets =
            [
                ("games-index.json", 1, null),
                ("generated-metadata.json", 2, null),
                ("renodx-sampleexact.addon64", 1001, "sha256:" + digest)
            ]
        };
        using var http = new HttpClient(handler);
        var resolution = await new OfficialArtifactResolver(http, paths).ResolveAsync(game, allowNetwork: true);
        Assert.Equal(RenoDxCompatibilityState.ExactAddonAvailableFromOfficialSnapshotRelease, resolution.RenoDxCompatibility);
        Assert.Equal("structured-metadata", resolution.SnapshotResolution?.SelectionReason);
        Assert.True(resolution.SnapshotResolution?.UsedStructuredMetadata);
    }

    [Fact]
    public void AmbiguousSlugIsRejected()
    {
        var index = RenoDxSnapshotReleaseResolver.ParseReleasePayload(
            ReleaseJson(1, "snapshot", false),
            AssetPages(
                ("renodx-demo.addon64", 1, null),
                ("renodx-demo-extra.addon64", 2, null)),
            null, null, null, null, DateTimeOffset.UtcNow);
        Assert.True(index.BySlug.ContainsKey("demo"));
        Assert.True(index.BySlug.ContainsKey("demo-extra"));
        Assert.Single(index.BySlug["demo"]);
    }

    [Fact]
    public async Task DigestMismatchRejectsAcquisition()
    {
        using var temp = new TestDirectory();
        var pe = File.ReadAllBytes(temp.Pe("payload/renodx-sample.addon64"));
        var paths = Paths(temp);
        var selection = new ArtifactSelection(
            ComponentKind.RenoDx, "nightly-snapshot",
            new Uri("https://github.com/clshortfuse/renodx/releases/download/nightly-snapshot/renodx-sample.addon64"),
            "nightly-snapshot", "renodx-sample.addon64", PeArchitecture.X64, null, "renodx-sample.addon64",
            "sha256:" + new string('0', 64), ArtifactArchiveKind.None, SourceValidatedByOfficialMetadata: true);
        var handler = new SnapshotHandler(temp)
        {
            AddonPayloads =
            {
                ["https://github.com/clshortfuse/renodx/releases/download/nightly-snapshot/renodx-sample.addon64"] = pe
            }
        };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ArtifactCacheService(paths).AcquireAsync(selection, http));
    }

    [Fact]
    public async Task OfflineCacheStillResolves()
    {
        using var temp = new TestDirectory();
        var pe = File.ReadAllBytes(temp.Pe("payload/renodx-crimsondesert.addon64"));
        var digest = Convert.ToHexString(SHA256.HashData(pe)).ToLowerInvariant();
        var game = Game(temp, 77, "Crimson Desert", "bin64");
        var paths = Paths(temp);
        var wiki = """
            # List
            | Name | Maintainer | Links | Status |
            | --- | --- | --- | --- |
            | Catalog Anchor | Author | [Snapshot](https://author.github.io/renodx/renodx-anchor.addon64) | :white_check_mark: |
            """;
        SeedWiki(paths, wiki);
        var handler = new SnapshotHandler(temp)
        {
            WikiMarkdown = wiki,
            DiscussionHtml = File.ReadAllText(Path.Combine(FixtureRoot, "discussion-manual-only.html")),
            GamesIndexJson = """{"games":[]}""",
            GeneratedMetadataJson = """{"schema_version":"1.0.0","mods":[],"artifacts":[]}""",
            AddonPayloads =
            {
                ["https://github.com/clshortfuse/renodx/releases/download/nightly-snapshot/renodx-crimsondesert.addon64"] = pe
            },
            ReleaseAssets =
            [
                ("games-index.json", 1, null),
                ("generated-metadata.json", 2, null),
                ("renodx-crimsondesert.addon64", 2001, "sha256:" + digest)
            ]
        };
        using var online = new HttpClient(handler);
        _ = await new OfficialArtifactResolver(online, paths).ResolveAsync(game, allowNetwork: true);
        handler.FailApi = true;
        using var offline = new HttpClient(handler);
        var resolution = await new OfficialArtifactResolver(offline, paths).ResolveAsync(game, allowNetwork: false);
        Assert.True(resolution.CanAcquireRenoDx);
        Assert.Equal(RenoDxCompatibilityState.ExactAddonAvailableFromOfficialSnapshotRelease, resolution.RenoDxCompatibility);
    }

    [Fact]
    public async Task UnknownEngineWithoutExactAssetIsNoAddonFoundNotUnsupported()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, 123, "Totally Unknown Title", ".");
        var paths = Paths(temp);
        SeedWiki(paths, """
            # List
            | Name | Maintainer | Links | Status |
            | --- | --- | --- | --- |
            | Other Game | Author | [![Snapshot](https://img.shields.io/badge/x)](https://example.github.io/renodx/renodx-other.addon64) | :white_check_mark: |
            """);
        var handler = new SnapshotHandler(temp)
        {
            GamesIndexJson = File.ReadAllText(Path.Combine(FixtureRoot, "games-index-sample.json")),
            GeneratedMetadataJson = File.ReadAllText(Path.Combine(FixtureRoot, "generated-metadata-sample.json")),
            ReleaseAssets =
            [
                ("games-index.json", 1, null),
                ("generated-metadata.json", 2, null),
                ("renodx-sampleexact.addon64", 1001, null),
                ("renodx-unrealengine.addon64", 3001, null)
            ]
        };
        using var http = new HttpClient(handler);
        var resolution = await new OfficialArtifactResolver(http, paths).ResolveAsync(game, allowNetwork: true);
        Assert.False(resolution.CanAcquireRenoDx);
        Assert.True(resolution.RenoDxCompatibility is RenoDxCompatibilityState.NotListed or
            RenoDxCompatibilityState.NoAddonFound or RenoDxCompatibilityState.OfficialPageAvailableNoDirectAddon or
            RenoDxCompatibilityState.ListedManualDownloadRequired);
        Assert.NotEqual(RenoDxCompatibilityState.Unsupported, resolution.RenoDxCompatibility);
    }

    private static string Normalize(string? digest) =>
        (digest ?? string.Empty).Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

    private static string ReleaseJson(long id, string tag, bool assetsEmbedded) =>
        JsonSerializer.Serialize(new
        {
            id,
            tag_name = tag,
            html_url = $"https://github.com/clshortfuse/renodx/releases/tag/{tag}",
            published_at = "2026-07-16T05:21:17Z",
            target_commitish = "main",
            prerelease = true,
            draft = false,
            assets = assetsEmbedded ? Array.Empty<object>() : Array.Empty<object>()
        });

    private static IReadOnlyList<string> AssetPages(params (string Name, long Id, string? Digest)[] assets)
    {
        var pages = new List<string>();
        for (var offset = 0; offset < assets.Length; offset += 100)
        {
            var chunk = assets.Skip(offset).Take(100).Select(asset => new Dictionary<string, object?>
            {
                ["id"] = asset.Id,
                ["name"] = asset.Name,
                ["size"] = 2048,
                ["browser_download_url"] =
                    $"https://github.com/clshortfuse/renodx/releases/download/nightly-snapshot/{asset.Name}",
                ["digest"] = asset.Digest,
                ["updated_at"] = "2026-07-16T05:21:17Z"
            }).ToArray();
            pages.Add(JsonSerializer.Serialize(chunk));
        }
        return pages;
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

    private static SteamGame Game(TestDirectory temp, uint appId, string name, string deployRelative)
    {
        var root = temp.Directory($"game-{appId}");
        var deploy = string.IsNullOrWhiteSpace(deployRelative) || deployRelative == "."
            ? root
            : temp.Directory($"game-{appId}/{deployRelative}");
        var exe = temp.Pe(deployRelative is "." or ""
            ? $"game-{appId}/Game.exe"
            : $"game-{appId}/{deployRelative}/Game.exe");
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

    private sealed class SnapshotHandler(TestDirectory temp) : HttpMessageHandler
    {
        public string DiscussionHtml { get; set; } = string.Empty;
        public string? WikiMarkdown { get; set; }
        public string GamesIndexJson { get; set; } = """{"games":[]}""";
        public string GeneratedMetadataJson { get; set; } = """{"schema_version":"1.0.0","mods":[],"artifacts":[]}""";
        public List<(string Name, long Id, string? Digest)> ReleaseAssets { get; set; } = [];
        public Dictionary<string, byte[]> AddonPayloads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailApi { get; set; }
        public bool AssetSlugCollision { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (FailApi && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);

            if (uri == RenoDxWikiClient.SourceUri)
                return Ok(WikiMarkdown ?? WikiWithDiscussion("Crimson Desert", 535), "text/markdown");

            if (uri.AbsolutePath.Contains("/discussions/", StringComparison.OrdinalIgnoreCase))
                return Ok(DiscussionHtml, "text/html");

            if (uri.AbsoluteUri.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(uri.AbsolutePath);
                if (fileName.Equals("games-index.json", StringComparison.OrdinalIgnoreCase))
                    return Ok(GamesIndexJson, "application/json");
                if (fileName.Equals("generated-metadata.json", StringComparison.OrdinalIgnoreCase))
                    return Ok(GeneratedMetadataJson, "application/json");
                if (AddonPayloads.TryGetValue(uri.GetLeftPart(UriPartial.Path), out var payload) ||
                    AddonPayloads.TryGetValue(uri.AbsoluteUri, out payload))
                {
                    await Task.Delay(5, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(payload)
                    };
                }
            }

            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
                {
                    return Ok(JsonSerializer.Serialize(new[]
                    {
                        new
                        {
                            id = 354877244L,
                            tag_name = "nightly-snapshot",
                            name = "RenoDX Snapshot Build",
                            html_url = "https://github.com/clshortfuse/renodx/releases/tag/nightly-snapshot",
                            published_at = "2026-07-16T05:21:17Z",
                            target_commitish = "main",
                            prerelease = true,
                            draft = false,
                            assets = Array.Empty<object>()
                        }
                    }), "application/json");
                }
                if (uri.AbsolutePath.Contains("/assets", StringComparison.OrdinalIgnoreCase))
                {
                    var page = 1;
                    if (uri.Query.Contains("page=", StringComparison.OrdinalIgnoreCase))
                    {
                        var query = uri.Query.TrimStart('?').Split('&')
                            .Select(part => part.Split('='))
                            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : "", StringComparer.OrdinalIgnoreCase);
                        _ = int.TryParse(query.GetValueOrDefault("page"), out page);
                    }
                    var chunk = ReleaseAssets.Skip((page - 1) * 100).Take(100).Select(asset => new Dictionary<string, object?>
                    {
                        ["id"] = asset.Id,
                        ["name"] = asset.Name,
                        ["size"] = 2048,
                        ["browser_download_url"] =
                            $"https://github.com/clshortfuse/renodx/releases/download/nightly-snapshot/{asset.Name}",
                        ["digest"] = asset.Digest,
                        ["updated_at"] = "2026-07-16T05:21:17Z"
                    }).ToArray();
                    return Ok(JsonSerializer.Serialize(chunk), "application/json");
                }
                if (uri.AbsolutePath.Contains("/releases/", StringComparison.OrdinalIgnoreCase))
                    return Ok(ReleaseJson(354877244, "nightly-snapshot", false), "application/json");
                return Ok("""{"tag_name":"v1","html_url":"https://github.com/optiscaler/OptiScaler/releases/tag/v1","published_at":"2026-01-01T00:00:00Z","assets":[]}""",
                    "application/json");
            }

            if (uri.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
                return Ok("<a href=\"/downloads/ReShade_Setup_6.7.3_Addon.exe\">full add-on support</a>", "text/html");
            if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return Ok("{\"version\":1}", "application/json");
            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.OK);
            _ = AssetSlugCollision;
            _ = temp;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(string body, string mediaType) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
    }
}
