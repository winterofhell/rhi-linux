using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed class ExecutableDetector
{
    private readonly GameAnalyzer analyzer = new();

    public IReadOnlyList<ExecutableCandidate> Rank(string gameRoot, string gameName)
    {
        var result = analyzer.Analyze(
            new GameInstall("steam", "local", gameName, gameRoot),
            new GameAnalysisOptions());
        return result.Fingerprint.Executables
            .Select(x => new ExecutableCandidate(x.Path, x.Score, x.Confidence, x.Architecture, x.Size, x.Reasons))
            .ToArray();
    }

    public GameEngine DetectEngine(string root)
    {
        var result = analyzer.Analyze(
            new GameInstall("steam", "local", Path.GetFileName(root), root),
            new GameAnalysisOptions(CollectComponentMarkers: false, OpenPeHeaders: false));
        return result.Fingerprint.Engine;
    }
}
