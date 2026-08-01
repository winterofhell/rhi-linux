using System.Globalization;
using System.Text;
using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed class ExecutableDetector
{
    private const int MaxDepth = 7;
    private static readonly string[] NegativeNames =
    [
        "launcher",
        "launchhelper",
        "crash",
        "report",
        "unins",
        "uninstall",
        "setup",
        "install",
        "benchmark",
        "config",
        "helper",
        "redist",
        "redistributable",
        "prereq",
        "eac",
        "easyanticheat",
        "battleye",
        "beclient",
        "beservice",
        "unitycrashhandler",
        "epicwebhelper"
    ];
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine", "Redist", "_CommonRedist", "DirectXRedist", "Prerequisites", "Installer", "CrashReportClient",
        "EasyAntiCheat", "BattlEye", "Extras", "Bonus", "Soundtrack", "Artbook"
    };

    public IReadOnlyList<ExecutableCandidate> Rank(string gameRoot, string gameName)
    {
        if (!Directory.Exists(gameRoot)) return [];
        var engine = DetectEngine(gameRoot);
        var candidates = new List<ExecutableCandidate>();
        foreach (var (path, depth) in EnumerateExecutables(gameRoot))
        {
            var architecture = PeReader.ReadArchitecture(path);
            var info = new FileInfo(path);
            var fileName = Path.GetFileNameWithoutExtension(path);
            var relative = Path.GetRelativePath(gameRoot, path);
            var normalizedFile = Normalize(fileName);
            var normalizedGame = Normalize(gameName);
            var reasons = new List<string>();
            var score = 0;

            if (architecture == PeArchitecture.X64) { score += 28; reasons.Add("64-bit PE executable"); }
            else if (architecture == PeArchitecture.X86) { score += 8; reasons.Add("32-bit PE executable"); }
            else { score -= 45; reasons.Add("file does not contain a recognized PE header"); }
            if (relative.Contains("Binaries/Win64", StringComparison.OrdinalIgnoreCase) || relative.Contains("Binaries\\Win64", StringComparison.OrdinalIgnoreCase))
            { score += 30; reasons.Add("Unreal Win64 binaries directory"); }
            if (fileName.Contains("Shipping", StringComparison.OrdinalIgnoreCase)) { score += 28; reasons.Add("Unreal Shipping executable"); }
            if (engine == GameEngine.Unity && HasUnityMarker(Path.GetDirectoryName(path)!)) { score += 22; reasons.Add("next to Unity runtime markers"); }
            if (engine == GameEngine.ReEngine && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "re_chunk_000.pak"))) { score += 24; reasons.Add("next to RE Engine data"); }
            var similarity = Similarity(normalizedFile, normalizedGame);
            var similarityScore = (int)Math.Round(similarity * 24);
            score += similarityScore;
            if (similarityScore >= 10) reasons.Add("filename resembles the Steam game name");
            var sizeScore = info.Length switch
            {
                >= 100 * 1024 * 1024 => 22,
                >= 20 * 1024 * 1024 => 16,
                >= 5 * 1024 * 1024 => 10,
                >= 1024 * 1024 => 3,
                _ => -8
            };
            score += sizeScore;
            reasons.Add(sizeScore > 0 ? "substantial executable size" : "very small executable");
            score -= depth * 2;
            if (depth == 0) { score += 7; reasons.Add("located at game root"); }
            var negative = NegativeNames.FirstOrDefault(x => normalizedFile.Contains(x, StringComparison.Ordinal));
            if (negative is not null) { score -= 55; reasons.Add($"looks like a {negative} utility"); }
            if (relative.Contains("Engine/Binaries", StringComparison.OrdinalIgnoreCase) || relative.Contains("Engine\\Binaries", StringComparison.OrdinalIgnoreCase))
            { score -= 50; reasons.Add("engine helper rather than game binary"); }

            candidates.Add(new ExecutableCandidate(path, score, Confidence(score), architecture, info.Length, reasons));
        }
        return candidates.OrderByDescending(x => x.Score).ThenByDescending(x => x.Size).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
    }

    public GameEngine DetectEngine(string root)
    {
        var shippingExecutable = FindFile(root, "*-Shipping.exe", 5);
        if (shippingExecutable is not null) return GameEngine.Unreal;
        if (FindFile(root, "*.upk", 4) is not null || FindFile(root, "*.u", 4) is not null)
            return GameEngine.UnrealLegacy;
        if (FindDirectory(root, "Binaries", 4) is not null) return GameEngine.UnrealLegacy;
        if (FindFile(root, "UnityPlayer.dll", 3) is not null || FindFile(root, "GameAssembly.dll", 3) is not null ||
            EnumerateDirectories(root, 2).Any(x => Path.GetFileName(x).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))) return GameEngine.Unity;
        if (FindFile(root, "re_chunk_000.pak", 3) is not null) return GameEngine.ReEngine;
        return GameEngine.Unknown;
    }

    private static IEnumerable<(string Path, int Depth)> EnumerateExecutables(string root)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var item))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(item.Path, "*.exe", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return (file, item.Depth);
            if (item.Depth >= MaxDepth) continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(item.Path).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var directory in directories)
            {
                if (SkippedDirectories.Contains(Path.GetFileName(directory))) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                queue.Enqueue((directory, item.Depth + 1));
            }
        }
    }

    private static bool HasUnityMarker(string directory) => File.Exists(Path.Combine(directory, "UnityPlayer.dll")) || File.Exists(Path.Combine(directory, "GameAssembly.dll"));
    private static DetectionConfidence Confidence(int score) => score switch { >= 60 => DetectionConfidence.High, >= 35 => DetectionConfidence.Medium, >= 10 => DetectionConfidence.Low, _ => DetectionConfidence.None };
    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }
    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)) return 1;
        var distance = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) distance[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            var previous = distance[0]; distance[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var saved = distance[j];
                distance[j] = Math.Min(Math.Min(distance[j] + 1, distance[j - 1] + 1), previous + (left[i - 1] == right[j - 1] ? 0 : 1));
                previous = saved;
            }
        }
        return 1d - (double)distance[^1] / Math.Max(left.Length, right.Length);
    }
    private static string? FindFile(string root, string pattern, int maxDepth) => EnumerateTree(root, maxDepth, d => Directory.EnumerateFiles(d, pattern).FirstOrDefault());
    private static string? FindDirectory(string root, string name, int maxDepth) => EnumerateTree(root, maxDepth, d => Directory.EnumerateDirectories(d).FirstOrDefault(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase)));
    private static IEnumerable<string> EnumerateDirectories(string root, int maxDepth)
    {
        var found = new List<string>(); EnumerateTree(root, maxDepth, d => { found.AddRange(Directory.EnumerateDirectories(d)); return null; }); return found;
    }
    private static string? EnumerateTree(string root, int maxDepth, Func<string, string?> find)
    {
        var queue = new Queue<(string, int)>(); queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var item))
        {
            try
            {
                var match = find(item.Item1); if (match is not null) return match;
                if (item.Item2 < maxDepth)
                    foreach (var child in Directory.EnumerateDirectories(item.Item1))
                        if (!SkippedDirectories.Contains(Path.GetFileName(child))) queue.Enqueue((child, item.Item2 + 1));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
