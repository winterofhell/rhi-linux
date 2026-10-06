using System.Net;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class DownloadLifecycleTests
{
    [Fact]
    public async Task DeadlineCoversResponseBodyAndCleansPartialDownloads()
    {
        using var temp = new TestDirectory();
        var cache = Cache(temp, TimeSpan.FromMilliseconds(250));
        var stream = new StalledStream();
        using var client = new HttpClient(new StreamHandler(stream));

        var error = await Assert.ThrowsAsync<ArtifactPipelineException>(() => cache.AcquireAsync(Selection(), client));

        Assert.Contains("time limit", error.UserSummary, StringComparison.Ordinal);
        Assert.True(stream.WasRead);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.StagingRoot));
        Assert.Empty(await cache.ListAsync());
    }

    [Fact]
    public async Task UserCancellationStopsBodyAndRemovesPartialFilesWithoutRetry()
    {
        using var temp = new TestDirectory();
        var cache = Cache(temp, TimeSpan.FromSeconds(30));
        var stream = new StalledStream();
        var handler = new StreamHandler(stream);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var download = cache.AcquireAsync(Selection(), client, cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.Equal(1, handler.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.StagingRoot));
        Assert.Empty(await cache.ListAsync());
    }

    private static ArtifactCacheService Cache(TestDirectory temp, TimeSpan timeout) =>
        new(new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") }), timeout);

    private static ArtifactSelection Selection() => new(ComponentKind.RenoDx, "fixture",
        new Uri("https://github.com/clshortfuse/renodx/releases/download/fixture/renodx-fixture.addon64"),
        "fixture", "renodx-fixture.addon64", PeArchitecture.X64, null, "renodx-fixture.addon64", null, ArtifactArchiveKind.None);

    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!WasRead)
            {
                WasRead = true;
                buffer.Span[..128].Clear();
                return 128;
            }
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
