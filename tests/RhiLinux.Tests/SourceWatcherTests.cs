using RhiLinux.Sources;

namespace RhiLinux.Tests;

public sealed class SourceWatcherTests
{
    [Fact]
    public void NormalizeKeepsLatestEventPerPath()
    {
        var older = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = older.AddSeconds(2);
        var events = new[]
        {
            new SourceWatchEvent("steam", "native", "/library/libraryfolders.vdf", WatcherChangeTypes.Changed, older),
            new SourceWatchEvent("steam", "native", "/library/libraryfolders.vdf", WatcherChangeTypes.Changed, newer),
            new SourceWatchEvent("heroic", "xdg", "/heroic/installed.json", WatcherChangeTypes.Created, older),
            new SourceWatchEvent("steam", "native", "/library/appmanifest_42.acf", WatcherChangeTypes.Deleted, newer)
        };

        var normalized = SourceWatcherCoordinator.Normalize(events);

        Assert.Equal(3, normalized.Count);
        var library = Assert.Single(normalized, item => item.Path.EndsWith("libraryfolders.vdf", StringComparison.Ordinal));
        Assert.Equal(newer, library.TimestampUtc);
        Assert.Contains(normalized, item => item.ProviderId == "heroic");
        Assert.Contains(normalized, item => item.Path.EndsWith("appmanifest_42.acf", StringComparison.Ordinal));
        Assert.Equal(["heroic", "steam", "steam"], normalized.Select(item => item.ProviderId).ToArray());
    }

    [Fact]
    public async Task DebouncedFlushEmitsNormalizedPendingEvents()
    {
        IReadOnlyList<SourceWatchEvent>? flushed = null;
        using var temp = new TestDirectory();
        var directory = temp.Directory("watch");
        var coordinator = new SourceWatcherCoordinator(
            events => flushed = events,
            TimeSpan.FromMilliseconds(50));

        coordinator.ReplaceTargets(
        [
            new SourceWatchTarget("steam", "fixture", directory, "*.acf", SourceWatchTargetKind.File)
        ]);
        Assert.Equal(1, coordinator.ActiveWatcherCount);

        var first = Path.Combine(directory, "appmanifest_1.acf");
        var second = Path.Combine(directory, "appmanifest_2.acf");
        await File.WriteAllTextAsync(first, "\"AppState\"{}");
        await File.WriteAllTextAsync(first, "\"AppState\"{\"appid\" \"1\"}");
        await File.WriteAllTextAsync(second, "\"AppState\"{}");

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (flushed is null && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.NotNull(flushed);
        var paths = flushed!.Select(item => item.Path).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Contains(first, paths);
        Assert.Contains(second, paths);
        Assert.All(flushed, item => Assert.Equal("steam", item.ProviderId));

        await coordinator.DisposeAsync();
    }
}
