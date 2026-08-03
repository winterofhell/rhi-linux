using System.Net;
using System.Net.Http.Headers;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class RenoDxWikiClientTests
{
    [Fact]
    public void ParsesArchitectureStatusAndOfficialWikiProvenance()
    {
        var records = RenoDxWikiClient.ParseMarkdown("""
            # List
            | Status | Links | Name | Maintainer |
            | :---: | :--- | :--- | :--- |
            | :white_check_mark: | [![Snapshot](https://img.shields.io/badge/Snapshot-blue)](https://author.github.io/renodx/renodx-fixture.addon64) | [Fixture Adventure](https://example.invalid/instructions) | Author |
            | :construction: | [Snapshot](https://github.com/author/renodx/releases/download/snapshot/renodx-legacy.addon32) | Legacy Adventure | Author |
            | unknown | [Snapshot](https://author.github.io/renodx/renodx-unknown.addon64) | Unknown Status | Author |
            | :white_check_mark: | [Nexus](https://nexusmods.com/game/mods/1) [Discord](https://discord.gg/example) | External Only | Author |
            """);

        Assert.Equal(3, records.Count);
        var working = Assert.Single(records, record => record.Name == "Fixture Adventure");
        Assert.Equal(RenoDxWikiStatus.Working, working.Status);
        Assert.Equal(RenoDxWikiRecordOrigin.ValidatedOfficialWiki, working.Origin);
        Assert.Equal("renodx-fixture.addon64", Path.GetFileName(working.Addon64Url!.AbsolutePath));
        Assert.Equal(working.Addon64Url, working.GetAddonUri(PeArchitecture.X64));
        Assert.Null(working.GetAddonUri(PeArchitecture.X86));

        var inProgress = Assert.Single(records, record => record.Name == "Legacy Adventure");
        Assert.Equal(RenoDxWikiStatus.InProgress, inProgress.Status);
        Assert.NotNull(inProgress.Addon32Url);
        Assert.Null(inProgress.Addon64Url);
        Assert.Equal(RenoDxWikiStatus.Unknown,
            Assert.Single(records, record => record.Name == "Unknown Status").Status);

        var catalog = RenoDxWikiClient.ParseCatalog("""
            # List
            | Status | Links | Name | Maintainer |
            | :---: | :--- | :--- | :--- |
            | :white_check_mark: | [![Snapshot](https://img.shields.io/badge/Snapshot-blue)](https://author.github.io/renodx/renodx-fixture.addon64) | [Fixture Adventure](https://example.invalid/instructions) | Author |
            | :construction: | [Snapshot](https://github.com/author/renodx/releases/download/snapshot/renodx-legacy.addon32) | Legacy Adventure | Author |
            | unknown | [Snapshot](https://author.github.io/renodx/renodx-unknown.addon64) | Unknown Status | Author |
            | :white_check_mark: | [Nexus](https://nexusmods.com/game/mods/1) [Discord](https://discord.gg/example) | External Only | Author |
            """);
        Assert.Contains(catalog, entry => entry.CanonicalName == "External Only" &&
            entry.SourceSection == RenoDxCatalogSection.ManualOnly &&
            !entry.DirectAutomaticDownloadAvailable);
    }

    [Fact]
    public void ComplementaryDuplicateRowsMergeButConflictingUrlsAreRejected()
    {
        var complementary = RenoDxWikiClient.ParseMarkdown("""
            # List
            | Name | Links | Status |
            | --- | --- | --- |
            | Dual Architecture | [x86](https://author.github.io/renodx/dual.addon32) | :white_check_mark: |
            | dual architecture | [x64](https://author.github.io/renodx/dual.addon64) | :construction: |
            """);

        var merged = Assert.Single(complementary);
        Assert.NotNull(merged.Addon32Url);
        Assert.NotNull(merged.Addon64Url);
        Assert.Equal(RenoDxWikiStatus.InProgress, merged.Status);

        var ambiguous = """
            # List
            | Name | Links | Status |
            | --- | --- | --- |
            | Ambiguous Game | [first](https://one.github.io/renodx/game.addon64) | :white_check_mark: |
            | ambiguous game | [second](https://two.github.io/renodx/game.addon64) | :white_check_mark: |
            """;
        var exception = Assert.Throws<InvalidDataException>(() => RenoDxWikiClient.ParseMarkdown(ambiguous));
        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://author.github.io/renodx/game.addon64")]
    [InlineData("https://author.github.io/renodx/game.addon64.exe")]
    public void RejectsUnsafeOrMisleadingAddonUrls(string url)
    {
        var markdown = $$"""
            # List
            | Name | Links | Status |
            | --- | --- | --- |
            | Unsafe Game | [Snapshot]({{url}}) | :white_check_mark: |
            """;

        Assert.Throws<InvalidDataException>(() => RenoDxWikiClient.ParseMarkdown(markdown));
    }

    [Fact]
    public void StopsBeforeRelatedAndDeprecatedSections()
    {
        var records = RenoDxWikiClient.ParseMarkdown("""
            # List
            | Name | Links | Status |
            | --- | --- | --- |
            | Supported Game | [Snapshot](https://author.github.io/renodx/supported.addon64) | :white_check_mark: |

            # Related Mods
            | Name | Links | Status |
            | --- | --- | --- |
            | Related Tool | [Download](https://author.github.io/renodx/related.addon64) | :white_check_mark: |

            # Deprecated mods
            | Name | Links | Status |
            | --- | --- | --- |
            | Supported Game | [Old](https://author.github.io/renodx/deprecated.addon64) | :white_check_mark: |
            """);

        var record = Assert.Single(records);
        Assert.Equal("Supported Game", record.Name);
        Assert.EndsWith("supported.addon64", record.Addon64Url!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesETagAndValidatedCacheForConditionalAndOfflineReads()
    {
        using var temp = new TestDirectory();
        var handler = new SequenceHandler(
            Response(HttpStatusCode.OK, ValidMarkdown("Cached Game", "cached.addon64"), "\"catalog-v1\""),
            Response(HttpStatusCode.NotModified));
        using var http = new HttpClient(handler);
        var client = new RenoDxWikiClient(http, Paths(temp));

        var first = await client.GetAsync(true);
        var notModified = await client.GetAsync(true);
        var offline = await client.GetAsync(false);

        Assert.Equal(RenoDxWikiFetchState.Online, first.State);
        Assert.True(first.IsCached);
        Assert.False(first.Changed);
        Assert.Equal(RenoDxWikiFetchState.Online, notModified.State);
        Assert.True(notModified.IsCached);
        Assert.Equal(RenoDxWikiFetchState.Offline, offline.State);
        Assert.True(offline.IsCached);
        Assert.Equal("Cached Game", Assert.Single(offline.Records).Name);
        Assert.True(handler.SawConditionalRequest);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CorruptRefreshPreservesLastKnownGoodCatalog()
    {
        using var temp = new TestDirectory();
        var handler = new SequenceHandler(
            Response(HttpStatusCode.OK, ValidMarkdown("Last Good Game", "good.addon64"), "\"good\""),
            Response(HttpStatusCode.OK, "# RenoDX\nThis is not a mod table.\n", "\"corrupt\""));
        using var http = new HttpClient(handler);
        var client = new RenoDxWikiClient(http, Paths(temp));

        var good = await client.GetAsync(true);
        var fallback = await client.GetAsync(true);
        var offline = await client.GetAsync(false);

        Assert.Equal("Last Good Game", Assert.Single(good.Records).Name);
        Assert.Equal(RenoDxWikiFetchState.Offline, fallback.State);
        Assert.True(fallback.IsCached);
        Assert.Contains("last known-good", fallback.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Last Good Game", Assert.Single(fallback.Records).Name);
        Assert.Equal("Last Good Game", Assert.Single(offline.Records).Name);
        Assert.Equal("\"good\"", offline.ETag);
    }

    [Fact]
    public async Task ChunkedResponseCannotGrowPastMetadataLimit()
    {
        using var temp = new TestDirectory();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new GeneratedStream(8L * 1024 * 1024 + 1))
        };
        using var http = new HttpClient(new SequenceHandler(response));
        var client = new RenoDxWikiClient(http, Paths(temp));

        var result = await client.GetAsync(true);

        Assert.Equal(RenoDxWikiFetchState.UnableToCheck, result.State);
        Assert.False(result.IsCached);
        Assert.Empty(result.Records);
        Assert.Contains("safety limit", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidMarkdown(string gameName, string fileName) => $$"""
        # List
        | Name | Maintainer | Links | Status |
        | --- | --- | --- | --- |
        | {{gameName}} | Author | [Snapshot](https://author.github.io/renodx/{{fileName}}) | :white_check_mark: |
        """;

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string? content = null,
        string? etag = null)
    {
        var response = new HttpResponseMessage(status);
        if (content is not null) response.Content = new StringContent(content);
        if (etag is not null) response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    private static XdgPaths Paths(TestDirectory temp) => new(temp.Path,
        new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public int RequestCount { get; private set; }
        public bool SawConditionalRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            SawConditionalRequest |= request.Headers.IfNoneMatch.Count > 0;
            if (responses.Count == 0)
                throw new InvalidOperationException("The test received an unexpected wiki request.");
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class GeneratedStream(long remaining) : Stream
    {
        private long remaining = remaining;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, remaining);
            if (read == 0) return 0;
            Array.Clear(buffer, offset, read);
            remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, remaining);
            if (read == 0) return ValueTask.FromResult(0);
            buffer.Span[..read].Clear();
            remaining -= read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
