using System.Globalization;
using System.Text;
using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed class GameAnalyzer : IGameAnalyzer
{
    private static readonly string[] NegativeNames =
    [
        "launcher", "launchhelper", "crash", "report", "unins", "uninstall", "setup", "install",
        "benchmark", "config", "helper", "redist", "redistributable", "prereq", "eac",
        "easyanticheat", "battleye", "beclient", "beservice", "unitycrashhandler", "epicwebhelper",
        "dedicated", "server", "editor"
    ];

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine", "Redist", "_CommonRedist", "DirectXRedist", "Prerequisites", "Installer", "CrashReportClient",
        "EasyAntiCheat", "BattlEye", "Extras", "Bonus", "Soundtrack", "Artbook", ".git", "node_modules"
    };

    private static readonly string[] GraphicsMarkers =
    [
        "d3d9.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "vulkan-1.dll", "opengl32.dll"
    ];

    private static readonly string[] UpscalerMarkers =
    [
        "nvngx_dlss.dll", "nvngx.dll", "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll",
        "libxess.dll", "libxess_dx11.dll", "OptiScaler.dll"
    ];

    public Task<GameAnalysisResult> AnalyzeAsync(
        GameInstall install,
        GameAnalysisOptions options,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Analyze(install, options, cancellationToken), cancellationToken);

    public GameAnalysisResult Analyze(
        GameInstall install,
        GameAnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(install.GameRoot);
        var evidence = new List<DiagnosticEvidence>();
        if (!Directory.Exists(root))
        {
            evidence.Add(new("missing-root", root, 0, "Game root does not exist.", EvidenceConfidence.High, "existence"));
            var empty = EmptyFingerprint(root, evidence, incomplete: true);
            return new GameAnalysisResult(empty, null, [], evidence);
        }

        var filesVisited = 0;
        var directoriesVisited = 0;
        var incomplete = false;
        var relevant = new List<string>();
        var executables = new List<(string Path, int Depth, FileInfo Info)>();
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sampleEntries = new List<string>();
        long sampleHash = 17;
        long topMtime = 0;
        var hasNativeLinux = false;
        var componentEvidence = new List<DiagnosticEvidence>();
        var graphicsEvidence = new List<DiagnosticEvidence>();
        var upscalerEvidence = new List<DiagnosticEvidence>();
        var antiCheatEvidence = new List<DiagnosticEvidence>();
        var apis = new HashSet<GraphicsApiKind>();
        var upscalers = new HashSet<UpscalerKind>();
        var components = new ComponentFootprints(false, false, false, false, false, false, false, [], []);
        var hasReShade = false;
        var hasRenoDx = false;
        var hasOptiScaler = false;
        var hasOptiPatcher = false;
        var hasLuma = false;
        var hasReFramework = false;
        var hasDxvk = false;
        var antiCheat = AntiCheatKind.None;
        var componentPaths = new List<string>();

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            directoriesVisited++;
            string[] files;
            string[] directories;
            try
            {
                files = Directory.EnumerateFiles(item.Path).Take(Math.Max(0, options.MaxFiles - filesVisited)).ToArray();
                directories = Directory.EnumerateDirectories(item.Path).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                incomplete = true;
                evidence.Add(new("unreadable", item.Path, 0, exception.Message, EvidenceConfidence.Medium, "filesystem"));
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filesVisited >= options.MaxFiles)
                {
                    incomplete = true;
                    evidence.Add(new("file-limit", root, 0, $"Stopped after {options.MaxFiles} files.", EvidenceConfidence.High, "limits"));
                    break;
                }

                filesVisited++;
                FileInfo info;
                try { info = new FileInfo(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    incomplete = true;
                    continue;
                }

                var name = info.Name;
                fileNames.Add(name);
                sampleHash = Mix(sampleHash, name, info.Length, info.LastWriteTimeUtc.Ticks);
                if (sampleEntries.Count < 64)
                    sampleEntries.Add($"{Path.GetRelativePath(root, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
                if (info.LastWriteTimeUtc.Ticks > topMtime) topMtime = info.LastWriteTimeUtc.Ticks;

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    executables.Add((file, item.Depth, info));

                if (item.Depth == 0 && IsElf(file))
                    hasNativeLinux = true;

                if (options.CollectComponentMarkers)
                    ObserveMarkers(
                        file, name, root, ref hasReShade, ref hasRenoDx, ref hasOptiScaler, ref hasOptiPatcher,
                        ref hasLuma, ref hasReFramework, ref hasDxvk, apis, upscalers,
                        componentEvidence, graphicsEvidence, upscalerEvidence, componentPaths, relevant);
            }

            if (filesVisited >= options.MaxFiles) break;
            if (item.Depth >= options.MaxDepth) continue;

            foreach (var directory in directories)
            {
                var directoryName = Path.GetFileName(directory);
                directoryNames.Add(directoryName);
                sampleHash = Mix(sampleHash, directoryName, 0, 0);
                if (directoryName.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase))
                {
                    antiCheat = AntiCheatKind.EasyAntiCheat;
                    antiCheatEvidence.Add(new("anti-cheat", directory, 40, "Easy Anti-Cheat directory present.", EvidenceConfidence.High, "directory-name"));
                }
                else if (directoryName.Equals("BattlEye", StringComparison.OrdinalIgnoreCase))
                {
                    antiCheat = AntiCheatKind.BattlEye;
                    antiCheatEvidence.Add(new("anti-cheat", directory, 40, "BattlEye directory present.", EvidenceConfidence.High, "directory-name"));
                }

                if (SkippedDirectories.Contains(directoryName)) continue;
                if (!options.FollowSymlinks)
                {
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        incomplete = true;
                        continue;
                    }
                }

                queue.Enqueue((directory, item.Depth + 1));
            }
        }

        if (fileNames.Contains("start_protected_game.exe"))
        {
            antiCheat = antiCheat == AntiCheatKind.None ? AntiCheatKind.Other : antiCheat;
            antiCheatEvidence.Add(new("anti-cheat", Path.Combine(root, "start_protected_game.exe"), 35,
                "Protected game launcher marker present.", EvidenceConfidence.High, "filename"));
        }

        var engine = DetectEngine(fileNames, directoryNames, root, evidence);
        var ranked = RankExecutables(root, install.Name, engine, executables, options.OpenPeHeaders);
        if (ranked.Count == 0)
            evidence.Add(new("no-exe", root, 0, "No Windows executable was discovered within scan limits.", EvidenceConfidence.High, "enumeration"));

        components = new ComponentFootprints(
            hasReShade, hasRenoDx, hasOptiScaler, hasOptiPatcher, hasLuma, hasReFramework, hasDxvk,
            componentPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            componentEvidence);

        var directoryFingerprint = new DirectoryFingerprint(
            root, filesVisited + directoriesVisited, sampleHash, topMtime,
            sampleEntries.Order(StringComparer.Ordinal).ToArray());

        var fingerprint = new GameFingerprint(
            root, root, DateTimeOffset.UtcNow, ranked, engine,
            new GraphicsApiEvidence(apis.OrderBy(x => x).ToArray(), graphicsEvidence),
            new UpscalerEvidence(upscalers.OrderBy(x => x).ToArray(), upscalerEvidence),
            new AntiCheatEvidence(antiCheat, antiCheat != AntiCheatKind.None, antiCheatEvidence),
            components, relevant.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            evidence, directoryFingerprint, hasNativeLinux, incomplete, filesVisited, directoriesVisited);

        var primary = SelectPrimary(ranked);
        var alternates = ranked
            .Where(x => primary is null || !string.Equals(x.Path, primary.Path, StringComparison.Ordinal))
            .Where(x => x.Kind is ExecutableKind.Primary or ExecutableKind.Alternate or ExecutableKind.Unknown)
            .Take(5)
            .ToArray();

        return new GameAnalysisResult(fingerprint, primary, alternates, evidence);
    }

    public static long ComputeDirectoryFingerprintHash(string gameRoot, int maxDepth = 2)
    {
        if (!Directory.Exists(gameRoot)) return 0;
        long hash = 17;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((Path.GetFullPath(gameRoot), 0));
        while (queue.TryDequeue(out var item))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(item.Path))
                {
                    var info = new FileInfo(file);
                    hash = Mix(hash, info.Name, info.Length, info.LastWriteTimeUtc.Ticks);
                }

                if (item.Depth >= maxDepth) continue;
                foreach (var directory in Directory.EnumerateDirectories(item.Path))
                {
                    var name = Path.GetFileName(directory);
                    if (SkippedDirectories.Contains(name)) continue;
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    hash = Mix(hash, name, 0, 0);
                    queue.Enqueue((directory, item.Depth + 1));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return hash;
    }

    private static ExecutableFingerprint? SelectPrimary(IReadOnlyList<ExecutableFingerprint> ranked)
    {
        var candidate = ranked.FirstOrDefault(x => x.Kind == ExecutableKind.Primary) ?? ranked.FirstOrDefault();
        if (candidate is null) return null;
        if (candidate.Confidence is DetectionConfidence.None or DetectionConfidence.Low && ranked.Count > 1)
            return null;
        return candidate.Kind is ExecutableKind.Launcher or ExecutableKind.Helper or ExecutableKind.Server or
               ExecutableKind.Benchmark or ExecutableKind.AntiCheat or ExecutableKind.Redistributable or ExecutableKind.Editor
            ? null
            : candidate;
    }

    private static IReadOnlyList<ExecutableFingerprint> RankExecutables(
        string root,
        string gameName,
        GameEngine engine,
        IReadOnlyList<(string Path, int Depth, FileInfo Info)> executables,
        bool openPe)
    {
        var ranked = new List<ExecutableFingerprint>();
        var normalizedGame = Normalize(gameName);
        foreach (var (path, depth, info) in executables)
        {
            var architecture = openPe ? PeReader.ReadArchitecture(path) : PeArchitecture.Unknown;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var relative = Path.GetRelativePath(root, path);
            var normalizedFile = Normalize(fileName);
            var executableDirectory = Path.GetDirectoryName(path)!;
            var reasons = new List<string>();
            var score = 0;
            var kind = ExecutableKind.Unknown;

            if (architecture == PeArchitecture.X64) { score += 28; reasons.Add("64-bit PE executable"); }
            else if (architecture == PeArchitecture.X86) { score += 8; reasons.Add("32-bit PE executable"); }
            else { score -= 45; reasons.Add("file does not contain a recognized PE header"); }

            if (relative.Contains("Binaries/Win64", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("Binaries\\Win64", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
                reasons.Add("Unreal Win64 binaries directory");
            }

            if (fileName.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
            {
                score += 28;
                reasons.Add("Unreal Shipping executable");
                kind = ExecutableKind.Primary;
            }

            if (engine == GameEngine.Unity &&
                (File.Exists(Path.Combine(executableDirectory, "UnityPlayer.dll")) ||
                 File.Exists(Path.Combine(executableDirectory, "GameAssembly.dll"))))
            {
                score += 22;
                reasons.Add("next to Unity runtime markers");
            }

            if (engine == GameEngine.ReEngine &&
                File.Exists(Path.Combine(executableDirectory, "re_chunk_000.pak")))
            {
                score += 24;
                reasons.Add("next to RE Engine data");
            }

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
            if (depth == 0)
            {
                score += 7;
                reasons.Add("located at game root");
            }

            var negative = NegativeNames.FirstOrDefault(x => normalizedFile.Contains(x, StringComparison.Ordinal));
            if (negative is not null)
            {
                score -= 55;
                reasons.Add($"looks like a {negative} utility");
                kind = negative switch
                {
                    "launcher" or "launchhelper" => ExecutableKind.Launcher,
                    "benchmark" => ExecutableKind.Benchmark,
                    "dedicated" or "server" => ExecutableKind.Server,
                    "editor" => ExecutableKind.Editor,
                    "eac" or "easyanticheat" or "battleye" or "beclient" or "beservice" => ExecutableKind.AntiCheat,
                    "redist" or "redistributable" or "prereq" or "setup" or "install" or "unins" or "uninstall" => ExecutableKind.Redistributable,
                    _ => ExecutableKind.Helper
                };
            }

            if (relative.Contains("Engine/Binaries", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("Engine\\Binaries", StringComparison.OrdinalIgnoreCase))
            {
                score -= 50;
                reasons.Add("engine helper rather than game binary");
                kind = ExecutableKind.Helper;
            }

            if (kind == ExecutableKind.Unknown)
                kind = score >= 35 ? ExecutableKind.Primary : ExecutableKind.Alternate;

            ranked.Add(new ExecutableFingerprint(
                path, relative, kind, score, Confidence(score), architecture, info.Length,
                new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)),
                reasons));
        }

        return ranked
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Size)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static GameEngine DetectEngine(
        HashSet<string> fileNames,
        HashSet<string> directoryNames,
        string root,
        List<DiagnosticEvidence> evidence)
    {
        if (fileNames.Any(name => name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Add(new("engine", root, 40, "Unreal Shipping executable marker.", EvidenceConfidence.High, "filename"));
            return GameEngine.Unreal;
        }

        if (fileNames.Any(name => name.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ||
                                  name.EndsWith(".u", StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Add(new("engine", root, 30, "Legacy Unreal package marker.", EvidenceConfidence.Medium, "filename"));
            return GameEngine.UnrealLegacy;
        }

        if (fileNames.Contains("UnityPlayer.dll") || fileNames.Contains("GameAssembly.dll") ||
            directoryNames.Any(name => name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Add(new("engine", root, 40, "Unity runtime markers.", EvidenceConfidence.High, "filename"));
            return GameEngine.Unity;
        }

        if (fileNames.Contains("re_chunk_000.pak"))
        {
            evidence.Add(new("engine", root, 40, "RE Engine package marker.", EvidenceConfidence.High, "filename"));
            return GameEngine.ReEngine;
        }

        if (directoryNames.Contains("Binaries"))
        {
            evidence.Add(new("engine", root, 10, "Binaries directory alone is weak Unreal-legacy evidence.", EvidenceConfidence.Low, "directory-name"));
            return GameEngine.UnrealLegacy;
        }

        evidence.Add(new("engine", root, 0, "No recognized engine markers were found within scan limits.", EvidenceConfidence.Low, "absence"));
        return GameEngine.Unknown;
    }

    private static void ObserveMarkers(
        string file,
        string name,
        string root,
        ref bool hasReShade,
        ref bool hasRenoDx,
        ref bool hasOptiScaler,
        ref bool hasOptiPatcher,
        ref bool hasLuma,
        ref bool hasReFramework,
        ref bool hasDxvk,
        HashSet<GraphicsApiKind> apis,
        HashSet<UpscalerKind> upscalers,
        List<DiagnosticEvidence> componentEvidence,
        List<DiagnosticEvidence> graphicsEvidence,
        List<DiagnosticEvidence> upscalerEvidence,
        List<string> componentPaths,
        List<string> relevant)
    {
        if (name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) && FileContainsAscii(file, "ReShade"))
        {
            hasReShade = true;
            componentPaths.Add(file);
            componentEvidence.Add(new("reshade", file, 20, "ReShade runtime marker.", EvidenceConfidence.Medium, "filename"));
            relevant.Add(Path.GetRelativePath(root, file));
        }

        if (name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase))
        {
            hasRenoDx = true;
            componentPaths.Add(file);
            componentEvidence.Add(new("renodx", file, 20, "RenoDX/addon marker.", EvidenceConfidence.Medium, "filename"));
            relevant.Add(Path.GetRelativePath(root, file));
        }

        if (name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))
        {
            hasOptiScaler = true;
            componentPaths.Add(file);
            componentEvidence.Add(new("optiscaler", file, 20, "OptiScaler marker.", EvidenceConfidence.Medium, "filename"));
            relevant.Add(Path.GetRelativePath(root, file));
        }

        if (name.Contains("OptiPatcher", StringComparison.OrdinalIgnoreCase))
        {
            hasOptiPatcher = true;
            componentPaths.Add(file);
            componentEvidence.Add(new("optipatcher", file, 15, "OptiPatcher marker.", EvidenceConfidence.Medium, "filename"));
        }

        if (name.Contains("Luma", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase))
        {
            hasLuma = true;
            componentPaths.Add(file);
            componentEvidence.Add(new("luma", file, 15, "Luma addon marker.", EvidenceConfidence.Low, "filename"));
        }

        if (name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase) &&
            (Directory.Exists(Path.Combine(Path.GetDirectoryName(file)!, "reframework")) ||
             name.Contains("reframework", StringComparison.OrdinalIgnoreCase)))
        {
            hasReFramework = true;
            componentPaths.Add(file);
            componentEvidence.Add(new("reframework", file, 15, "REFramework marker.", EvidenceConfidence.Low, "filename"));
        }

        if (name.Equals("dxvk.conf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) && FileContainsAscii(file, "DXVK"))
        {
            hasDxvk = true;
            componentEvidence.Add(new("dxvk", file, 10, "DXVK marker.", EvidenceConfidence.Low, "filename"));
        }

        foreach (var marker in GraphicsMarkers)
        {
            if (!name.Equals(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var api = marker switch
            {
                "d3d9.dll" => GraphicsApiKind.Direct3D9,
                "d3d11.dll" => GraphicsApiKind.Direct3D11,
                "d3d12.dll" => GraphicsApiKind.Direct3D12,
                "vulkan-1.dll" => GraphicsApiKind.Vulkan,
                "opengl32.dll" => GraphicsApiKind.OpenGl,
                "dxgi.dll" => GraphicsApiKind.Direct3D11,
                _ => GraphicsApiKind.Unknown
            };
            if (api != GraphicsApiKind.Unknown)
            {
                apis.Add(api);
                graphicsEvidence.Add(new("graphics-api", file, 10, $"Found {marker}.", EvidenceConfidence.Medium, "filename"));
            }
        }

        foreach (var marker in UpscalerMarkers)
        {
            if (!name.Equals(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var kind = marker.Contains("dlss", StringComparison.OrdinalIgnoreCase) ? UpscalerKind.Dlss :
                marker.Contains("fidelityfx", StringComparison.OrdinalIgnoreCase) ? UpscalerKind.Fsr :
                marker.Contains("xess", StringComparison.OrdinalIgnoreCase) ? UpscalerKind.XeSS :
                marker.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) ? UpscalerKind.OptiScaler :
                UpscalerKind.Unknown;
            if (kind == UpscalerKind.Unknown) continue;
            upscalers.Add(kind);
            upscalerEvidence.Add(new("upscaler", file, 10, $"Found {marker}.", EvidenceConfidence.Medium, "filename"));
        }
    }

    private static bool FileContainsAscii(string path, string marker)
        => BinaryMarkerScanner.ContainsAscii(path, marker, 8 * 1024 * 1024);

    private static bool IsElf(string path)
    {
        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Read(magic) == magic.Length && magic.SequenceEqual("\u007fELF"u8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static GameFingerprint EmptyFingerprint(string root, List<DiagnosticEvidence> evidence, bool incomplete) =>
        new(root, root, DateTimeOffset.UtcNow, [], GameEngine.Unknown,
            new GraphicsApiEvidence([], []), new UpscalerEvidence([], []),
            new AntiCheatEvidence(AntiCheatKind.None, false, []),
            new ComponentFootprints(false, false, false, false, false, false, false, [], []),
            [], evidence, new DirectoryFingerprint(root, 0, 0, 0, []), false, incomplete, 0, 0);

    private static DetectionConfidence Confidence(int score) => score switch
    {
        >= 60 => DetectionConfidence.High,
        >= 35 => DetectionConfidence.Medium,
        >= 10 => DetectionConfidence.Low,
        _ => DetectionConfidence.None
    };

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
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
            var previous = distance[0];
            distance[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var saved = distance[j];
                distance[j] = Math.Min(Math.Min(distance[j] + 1, distance[j - 1] + 1), previous + (left[i - 1] == right[j - 1] ? 0 : 1));
                previous = saved;
            }
        }

        return 1d - (double)distance[^1] / Math.Max(left.Length, right.Length);
    }

    private static long Mix(long hash, string name, long size, long ticks)
    {
        hash = StableHash.Combine(hash, StableHash.OrdinalIgnoreCase(name));
        hash = StableHash.Combine(hash, size);
        return StableHash.Combine(hash, ticks);
    }
}
