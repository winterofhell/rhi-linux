namespace RhiLinux.Core;

public static class SourceRootDiscovery
{
    public static GameSourceDiscoveryContext CreateContext(
        string? home = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        IReadOnlyList<string>? customRoots = null,
        IReadOnlySet<string>? enabledProviders = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customRootsByProvider = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        environment ??= CaptureEnvironment();
        var config = ResolveXdg(environment, "XDG_CONFIG_HOME", Path.Combine(home, ".config"), home);
        var data = ResolveXdg(environment, "XDG_DATA_HOME", Path.Combine(home, ".local", "share"), home);
        var cache = ResolveXdg(environment, "XDG_CACHE_HOME", Path.Combine(home, ".cache"), home);
        return new GameSourceDiscoveryContext(
            home,
            config,
            data,
            cache,
            environment,
            customRoots ?? [],
            enabledProviders,
            customRootsByProvider);
    }

    public static IReadOnlyList<GameSourceRoot> ResolveCandidates(
        string providerId,
        IEnumerable<(string Path, SourceRootKind Kind)> candidates,
        IReadOnlyList<string>? customRoots = null,
        string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ordered = new List<(string Path, SourceRootKind Kind)>();
        if (customRoots is not null)
        {
            foreach (var root in customRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
                ordered.Add((root, SourceRootKind.Custom));
        }

        ordered.AddRange(candidates);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<GameSourceRoot>();
        foreach (var (original, kind) in ordered)
        {
            var expanded = XdgPaths.ExpandHome(original, home);
            string canonical;
            try
            {
                canonical = GameIdentity.NormalizePath(expanded);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                results.Add(new(
                    providerId, original, expanded, expanded, kind,
                    false, false, false, $"Path could not be normalized: {exception.Message}"));
                continue;
            }

            var exists = Directory.Exists(canonical);
            var readable = exists && CanReadDirectory(canonical);
            var deduplicated = exists && !seen.Add(canonical);
            string? skip = null;
            if (!exists) skip = "Source root does not exist.";
            else if (!readable) skip = "Source root is not readable.";
            else if (deduplicated) skip = "Deduplicated canonical source root.";

            results.Add(new(
                providerId, original, expanded, canonical, kind,
                exists, readable, deduplicated, skip));
        }

        return results;
    }

    public static string FlatpakConfig(string home, string appId, params string[] relative) =>
        Path.Combine(new[] { home, ".var", "app", appId, "config" }.Concat(relative).ToArray());

    public static string FlatpakData(string home, string appId, params string[] relative) =>
        Path.Combine(new[] { home, ".var", "app", appId, "data" }.Concat(relative).ToArray());

    public static string SourceFingerprint(params string?[] paths)
    {
        long hash = 17;
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Order(StringComparer.Ordinal))
        {
            hash = Mix(hash, path!);
            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path!);
                    hash = Mix(hash, info.Length.ToString());
                    hash = Mix(hash, info.LastWriteTimeUtc.Ticks.ToString());
                }
                else if (Directory.Exists(path))
                {
                    var info = new DirectoryInfo(path!);
                    hash = Mix(hash, info.LastWriteTimeUtc.Ticks.ToString());
                }
                else
                {
                    hash = Mix(hash, "missing");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                hash = Mix(hash, "unreadable");
            }
        }

        return hash.ToString("x16");
    }

    public static DateTimeOffset GetMetadataTimestamp(string path)
    {
        try
        {
            if (File.Exists(path))
                return new DateTimeOffset(DateTime.SpecifyKind(File.GetLastWriteTimeUtc(path), DateTimeKind.Utc));
            if (Directory.Exists(path))
                return new DateTimeOffset(DateTime.SpecifyKind(Directory.GetLastWriteTimeUtc(path), DateTimeKind.Utc));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return DateTimeOffset.UnixEpoch;
    }

    public static bool CanReadDirectory(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Any();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long Mix(long hash, string value) => StableHash.Combine(hash, value);

    private static string ResolveXdg(
        IReadOnlyDictionary<string, string?> environment,
        string key,
        string fallback,
        string home) =>
        GameIdentity.NormalizePath(XdgPaths.ReadBaseDirectory(environment, key, home) ?? fallback);

    private static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
                result[key] = entry.Value as string;
        }

        return result;
    }
}
