using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed record ProtonPrefixInfo(
    string PrefixPath,
    bool Exists,
    bool HasDriveC,
    DateTimeOffset? LastWriteUtc,
    long? SizeBytesApprox,
    string? Owner,
    string Filesystem,
    long? FreeBytes,
    IReadOnlyList<string> Notes);

public sealed record CompatibilityToolInfo(
    string Name,
    string Path,
    string Kind);

public sealed record ProtonInspection(
    InstalledGame Game,
    ProtonPrefixInfo Prefix,
    IReadOnlyList<CompatibilityToolInfo> AvailableTools,
    string? SelectedToolHint,
    string Summary);

public static class ProtonInspector
{
    public static ProtonInspection Inspect(InstalledGame game)
    {
        var prefixPath = game.ProtonPrefix;
        var exists = Directory.Exists(prefixPath);
        var driveC = exists && Directory.Exists(Path.Combine(prefixPath, "drive_c"));
        DateTimeOffset? mtime = null;
        long? size = null;
        string? owner = null;
        var notes = new List<string>();
        if (exists)
        {
            try
            {
                mtime = Directory.GetLastWriteTimeUtc(prefixPath);
                size = ApproximateSize(prefixPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Prefix metadata incomplete: {exception.Message}");
            }
        }
        else
            notes.Add("Proton prefix has not been created yet. Launch the game once in Steam.");

        var freeBytes = TryGetFreeBytes(prefixPath);
        var tools = DiscoverCompatibilityTools(game.SteamRoot ?? string.Empty);
        var selected = tools.FirstOrDefault(tool => tool.Kind.Contains("proton", StringComparison.OrdinalIgnoreCase))?.Name;
        var summary = exists
            ? driveC
                ? $"Proton prefix is present with drive_c ({tools.Count} compatibility tools discovered)."
                : "Proton prefix exists but drive_c is missing."
            : "Proton prefix is not present yet.";

        return new ProtonInspection(
            game,
            new ProtonPrefixInfo(prefixPath, exists, driveC, mtime, size, owner, DetectFilesystem(prefixPath), freeBytes, notes),
            tools,
            selected,
            summary);
    }

    public static IReadOnlyList<CompatibilityToolInfo> DiscoverCompatibilityTools(string steamRoot)
    {
        var results = new List<CompatibilityToolInfo>();
        foreach (var candidate in new[]
                 {
                     Path.Combine(steamRoot, "compatibilitytools.d"),
                     Path.Combine(steamRoot, "steamapps", "common")
                 })
        {
            if (!Directory.Exists(candidate)) continue;
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(candidate))
                {
                    var name = Path.GetFileName(directory);
                    if (name.Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
                        File.Exists(Path.Combine(directory, "compatibilitytool.vdf")))
                    {
                        var kind = name.Contains("GE", StringComparison.OrdinalIgnoreCase) ? "proton-ge" :
                            name.Contains("Experimental", StringComparison.OrdinalIgnoreCase) ? "proton-experimental" :
                            name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ? "proton-valve" :
                            "compatibility-tool";
                        results.Add(new(name, directory, kind));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return results
            .GroupBy(tool => tool.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static long ApproximateSize(string path)
    {
        long total = 0;
        var remaining = 5_000;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            if (--remaining < 0) break;
            try { total += new FileInfo(file).Length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return total;
    }

    private static long? TryGetFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path));
            if (string.IsNullOrWhiteSpace(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string DetectFilesystem(string path)
    {
        try
        {
            if (!File.Exists("/proc/mounts")) return "unknown";
            var full = Path.GetFullPath(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
            string? best = null;
            var bestLength = -1;
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                var mount = parts[1].Replace("\\040", " ", StringComparison.Ordinal);
                if (full.StartsWith(mount, StringComparison.Ordinal) && mount.Length > bestLength)
                {
                    best = parts[2];
                    bestLength = mount.Length;
                }
            }

            return best ?? "unknown";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}
