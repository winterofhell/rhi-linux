using System.Globalization;

namespace RhiLinux.Steam;

public enum SteamArtworkKind
{
    Hero,
    Header,
    Capsule,
    Logo
}

public static class SteamArtworkLocator
{
    private const int MaximumSearchDepth = 2;
    private const long MaximumFileSize = 32 * 1024 * 1024;
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public static string? Find(string? steamRoot, uint? appId, SteamArtworkKind kind)
    {
        if (appId is not { } id || id == 0) return null;
        foreach (var root in CandidateRoots(steamRoot))
        {
            if (FindInRoot(root, id, kind) is { } path) return path;
        }
        return null;
    }

    public static IEnumerable<string> CandidateRoots(string? preferred)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string?[] roots =
        [
            preferred,
            Environment.GetEnvironmentVariable("STEAM_DIR"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam")
        ];
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string resolved;
            try
            {
                resolved = Directory.ResolveLinkTarget(root, true)?.FullName ?? Path.GetFullPath(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            if (seen.Add(resolved.TrimEnd(Path.DirectorySeparatorChar))) yield return resolved;
        }
    }

    private static string? FindInRoot(string steamRoot, uint id, SteamArtworkKind kind)
    {
        var cache = Path.Combine(steamRoot, "appcache", "librarycache");
        if (!Directory.Exists(cache)) return null;
        var appDirectory = Path.Combine(cache, id.ToString(CultureInfo.InvariantCulture));
        var hasAppDirectory = Directory.Exists(appDirectory);
        foreach (var candidateKind in FallbackOrder(kind))
        {
            foreach (var name in FlatNames(id, candidateKind))
            {
                var flat = Path.Combine(cache, name);
                if (IsUsable(flat)) return flat;
            }
            if (!hasAppDirectory) continue;
            var names = NestedNames(candidateKind);
            foreach (var name in names)
            {
                var direct = Path.Combine(appDirectory, name);
                if (IsUsable(direct)) return direct;
            }
            if (Search(appDirectory, names, 1) is { } nested) return nested;
        }
        return hasAppDirectory && kind != SteamArtworkKind.Logo ? LargestImage(appDirectory) : null;
    }

    private static SteamArtworkKind[] FallbackOrder(SteamArtworkKind kind) => kind switch
    {
        SteamArtworkKind.Hero => [SteamArtworkKind.Hero, SteamArtworkKind.Header],
        SteamArtworkKind.Header => [SteamArtworkKind.Header, SteamArtworkKind.Hero],
        SteamArtworkKind.Capsule => [SteamArtworkKind.Capsule, SteamArtworkKind.Header],
        _ => [kind]
    };

    private static IEnumerable<string> FlatNames(uint id, SteamArtworkKind kind) => kind switch
    {
        SteamArtworkKind.Hero => [$"{id}_library_hero.jpg", $"{id}_library_hero.png"],
        SteamArtworkKind.Header => [$"{id}_header.jpg", $"{id}_header.png"],
        SteamArtworkKind.Capsule => [$"{id}_library_600x900.jpg", $"{id}_library_600x900_2x.jpg"],
        SteamArtworkKind.Logo => [$"{id}_logo.png"],
        _ => []
    };

    private static string[] NestedNames(SteamArtworkKind kind) => kind switch
    {
        SteamArtworkKind.Hero => ["library_hero.jpg", "library_hero.png"],
        SteamArtworkKind.Header => ["header.jpg", "library_header.jpg", "header.png", "library_header.png"],
        SteamArtworkKind.Capsule => ["library_600x900.jpg", "library_600x900_2x.jpg", "library_capsule.jpg"],
        SteamArtworkKind.Logo => ["logo.png", "library_logo.png"],
        _ => []
    };

    private static string? LargestImage(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = MaximumSearchDepth, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(file => ImageExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase) &&
                               !file.Name.Contains("blur", StringComparison.OrdinalIgnoreCase) &&
                               !file.Name.StartsWith("logo", StringComparison.OrdinalIgnoreCase) &&
                               file.Length is > 0 and < MaximumFileSize)
                .OrderByDescending(file => file.Length)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Search(string directory, IReadOnlyList<string> names, int depth)
    {
        if (depth > MaximumSearchDepth) return null;
        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var child in children)
        {
            if (new DirectoryInfo(child).LinkTarget is not null) continue;
            foreach (var name in names)
            {
                var candidate = Path.Combine(child, name);
                if (IsUsable(candidate)) return candidate;
            }
            if (Search(child, names, depth + 1) is { } nested) return nested;
        }
        return null;
    }

    private static bool IsUsable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length is > 0 and < MaximumFileSize;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
