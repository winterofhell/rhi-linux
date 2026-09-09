using RhiLinux.Core;
using RhiLinux.Sources;
using System.Collections.Concurrent;

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

    [Theory]
    [InlineData(WatcherChangeTypes.Created, WatcherChangeTypes.Changed, WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Created, WatcherChangeTypes.Deleted, WatcherChangeTypes.Deleted)]
    [InlineData(WatcherChangeTypes.Deleted, WatcherChangeTypes.Created, WatcherChangeTypes.Changed)]
    [InlineData(WatcherChangeTypes.Changed, WatcherChangeTypes.Renamed, WatcherChangeTypes.Renamed)]
    public void NormalizePreservesMeaningfulChangeSemantics(
        WatcherChangeTypes first,
        WatcherChangeTypes second,
        WatcherChangeTypes expected)
    {
        var path = "/fixture/game.json";
        var older = DateTimeOffset.UnixEpoch;
        var normalized = SourceWatcherCoordinator.Normalize(
        [
            new("heroic", "root", path, first, older),
            new("heroic", "root", path, second, older.AddSeconds(1), "/fixture/old.json")
        ]);

        var item = Assert.Single(normalized);
        Assert.Equal(expected, item.ChangeType);
        Assert.Equal("/fixture/old.json", item.OldPath);
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

    [Fact]
    public async Task RecursiveTargetsObserveNestedDirectories()
    {
        IReadOnlyList<SourceWatchEvent>? flushed = null;
        using var temp = new TestDirectory();
        var root = temp.Directory("bottles");
        Directory.CreateDirectory(Path.Combine(root, "my-bottle"));
        var coordinator = new SourceWatcherCoordinator(
            events => flushed = events,
            TimeSpan.FromMilliseconds(50));

        coordinator.ReplaceTargets(
        [
            new SourceWatchTarget("bottles", "fixture", root, "bottle.yml", SourceWatchTargetKind.Pattern, true)
        ]);

        var nested = Path.Combine(root, "my-bottle", "bottle.yml");
        await File.WriteAllTextAsync(nested, "Name: my-bottle");

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (flushed is null && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.NotNull(flushed);
        Assert.Contains(flushed!, item => item.Path.Equals(nested, StringComparison.Ordinal));

        await coordinator.DisposeAsync();
    }

    [Fact]
    public void ReplacingASingleTargetKeepsUnrelatedWatchersAlive()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steamapps");
        var heroic = temp.Directory("heroic");
        var coordinator = new SourceWatcherCoordinator(_ => { }, TimeSpan.FromMilliseconds(50));

        coordinator.ReplaceTargets(
        [
            new SourceWatchTarget("steam", "fixture", steam, "*.acf", SourceWatchTargetKind.Pattern),
            new SourceWatchTarget("heroic", "fixture", heroic, "installed.json", SourceWatchTargetKind.File)
        ]);

        Assert.Equal(2, coordinator.ActiveWatcherCount);

        coordinator.ReplaceTargets(
        [
            new SourceWatchTarget("steam", "fixture", steam, "*.acf", SourceWatchTargetKind.Pattern),
            new SourceWatchTarget("legendary", "fixture", temp.Directory("legendary"), "installed.json", SourceWatchTargetKind.File)
        ]);

        Assert.Equal(2, coordinator.ActiveWatcherCount);
        Assert.Equal(2, coordinator.DesiredTargetCount);
        coordinator.ReplaceTargets([]);
        Assert.Equal(0, coordinator.ActiveWatcherCount);
    }

    [Fact]
    public void BottlesTargetsWatchBottleMetadataWithoutRecursingIntoDriveC()
    {
        using var temp = new TestDirectory();
        var rootPath = temp.Directory("bottles");
        var bottle = temp.Directory("bottles", "GamingBottle");
        temp.Directory("bottles", "GamingBottle", "drive_c");
        var root = new GameSourceRoot(
            "bottles", rootPath, rootPath, rootPath, SourceRootKind.Xdg,
            true, true, false, null);

        var targets = SourceWatchTargets.ForBottles(root);

        Assert.Contains(targets, target => target.Directory == rootPath && target.Kind == SourceWatchTargetKind.Directory);
        Assert.Contains(targets, target => target.Directory == bottle && target.Filter == "bottle.yml");
        Assert.DoesNotContain(targets, target => target.Directory.Contains("drive_c", StringComparison.OrdinalIgnoreCase));
        Assert.All(targets, target => Assert.False(target.Recursive));
    }

    [Fact]
    public async Task BottlesImmediateDirectoryWatcherObservesBottleMetadata()
    {
        using var temp = new TestDirectory();
        var rootPath = temp.Directory("bottles");
        var bottle = temp.Directory("bottles", "GamingBottle");
        var metadata = Path.Combine(bottle, "bottle.yml");
        var root = new GameSourceRoot(
            "bottles", rootPath, rootPath, rootPath, SourceRootKind.Xdg,
            true, true, false, null);
        IReadOnlyList<SourceWatchEvent>? received = null;
        var coordinator = new SourceWatcherCoordinator(
            events => received = events,
            TimeSpan.FromMilliseconds(30));
        coordinator.ReplaceTargets(SourceWatchTargets.ForBottles(root));

        await File.WriteAllTextAsync(metadata, "Name: GamingBottle");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (received is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Contains(received!, item => item.Path == metadata && item.ProviderId == "bottles");
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task MissingTargetAndCallbackFailuresAreObservable()
    {
        using var temp = new TestDirectory();
        var diagnostics = new List<SourceWatcherDiagnostic>();
        var missing = temp.Combine("missing");
        var coordinator = new SourceWatcherCoordinator(
            (_, _) => throw new InvalidOperationException("fixture callback failure"),
            TimeSpan.FromMilliseconds(30),
            diagnostic => diagnostics.Add(diagnostic));

        coordinator.ReplaceTargets(
        [
            new SourceWatchTarget("heroic", "missing-root", missing, "installed.json", SourceWatchTargetKind.File)
        ]);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == SourceWatcherDiagnosticCode.TargetMissing);
        Assert.Equal(1, coordinator.DesiredTargetCount);
        Assert.Equal(0, coordinator.ActiveWatcherCount);

        var available = temp.Directory("available");
        coordinator.ReplaceTargets(
        [
            new SourceWatchTarget("heroic", "available-root", available, "*.json", SourceWatchTargetKind.Pattern)
        ]);
        await File.WriteAllTextAsync(Path.Combine(available, "installed.json"), "{}");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!diagnostics.Any(item => item.Code == SourceWatcherDiagnosticCode.CallbackFailed) &&
               DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == SourceWatcherDiagnosticCode.CallbackFailed &&
            diagnostic.ExceptionType == nameof(InvalidOperationException));
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task MissingTargetIsActivatedWhenDirectoryAppears()
    {
        using var temp = new TestDirectory();
        var directory = temp.Combine("appears-later");
        var coordinator = new SourceWatcherCoordinator(_ => { }, TimeSpan.FromMilliseconds(30));
        coordinator.ReplaceTargets(
        [
            new SourceWatchTarget("heroic", "fixture", directory, "*.json", SourceWatchTargetKind.Pattern)
        ]);
        Assert.Equal(0, coordinator.ActiveWatcherCount);

        Directory.CreateDirectory(directory);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (coordinator.ActiveWatcherCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(1, coordinator.ActiveWatcherCount);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DisposalDuringDebounceCancelsPendingCallback()
    {
        using var temp = new TestDirectory();
        var directory = temp.Directory("debounce");
        var callbacks = 0;
        var coordinator = new SourceWatcherCoordinator(
            _ => Interlocked.Increment(ref callbacks),
            TimeSpan.FromSeconds(1));
        coordinator.ReplaceTargets(
        [
            new SourceWatchTarget("heroic", "fixture", directory, "*.json", SourceWatchTargetKind.Pattern)
        ]);
        await File.WriteAllTextAsync(Path.Combine(directory, "installed.json"), "{}");
        await Task.Delay(30);

        await coordinator.DisposeAsync();
        await Task.Delay(50);

        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task OverflowRecreatesOnlyFailedWatcherAndKeepsUnrelatedTargets()
    {
        using var temp = new TestDirectory();
        var diagnostics = new ConcurrentQueue<SourceWatcherDiagnostic>();
        var steam = new SourceWatchTarget(
            "steam", "steam-root", temp.Directory("steam"), "*.acf", SourceWatchTargetKind.Pattern);
        var heroic = new SourceWatchTarget(
            "heroic", "heroic-root", temp.Directory("heroic"), "*.json", SourceWatchTargetKind.Pattern);
        var lutris = new SourceWatchTarget(
            "lutris", "lutris-root", temp.Directory("lutris"), "*.yml", SourceWatchTargetKind.Pattern);
        var coordinator = new SourceWatcherCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(30),
            diagnostics.Enqueue);
        coordinator.ReplaceTargets([steam, heroic, lutris]);

        coordinator.HandleWatcherFailure(lutris, new InternalBufferOverflowException("fixture overflow"));

        Assert.True(coordinator.IsWatching(steam));
        Assert.True(coordinator.IsWatching(heroic));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!coordinator.IsWatching(lutris) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(coordinator.IsWatching(lutris));
        Assert.Equal(3, coordinator.DesiredTargetCount);
        Assert.Contains(diagnostics, item => item.Code == SourceWatcherDiagnosticCode.WatcherOverflow);
        Assert.Contains(diagnostics, item => item.Code == SourceWatcherDiagnosticCode.WatcherRecreated);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task FailedWatcherRecreationIsBoundedAndLeavesOtherWatchersAlive()
    {
        using var temp = new TestDirectory();
        var diagnostics = new ConcurrentQueue<SourceWatcherDiagnostic>();
        var steam = new SourceWatchTarget(
            "steam", "steam-root", temp.Directory("steam"), "*.acf", SourceWatchTargetKind.Pattern);
        var heroic = new SourceWatchTarget(
            "heroic", "heroic-root", temp.Directory("heroic"), "*.json", SourceWatchTargetKind.Pattern);
        var lutrisDirectory = temp.Directory("lutris");
        var lutris = new SourceWatchTarget(
            "lutris", "lutris-root", lutrisDirectory, "*.yml", SourceWatchTargetKind.Pattern);
        var coordinator = new SourceWatcherCoordinator(
            _ => { },
            TimeSpan.FromMilliseconds(30),
            diagnostics.Enqueue);
        coordinator.ReplaceTargets([steam, heroic, lutris]);

        coordinator.HandleWatcherFailure(lutris, new IOException("fixture path disappeared"));
        Directory.Delete(lutrisDirectory);
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!diagnostics.Any(item => item.Code == SourceWatcherDiagnosticCode.WatcherRecreationExhausted) &&
               DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Contains(diagnostics, item => item.Code == SourceWatcherDiagnosticCode.WatcherRecreationExhausted);
        Assert.True(coordinator.IsWatching(steam));
        Assert.True(coordinator.IsWatching(heroic));
        Assert.False(coordinator.IsWatching(lutris));
        Assert.Equal(3, coordinator.DesiredTargetCount);
        await coordinator.DisposeAsync();
    }
}
