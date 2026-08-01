using RhiLinux.Core;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class ExecutableDetectorTests
{
    [Fact]
    public void PrefersUnrealShippingBinaryOverLauncher()
    {
        using var temp = new TestDirectory(); temp.Pe("launcher.exe", size: 30 * 1024 * 1024);
        var shipping = temp.Pe("Project/Binaries/Win64/Example-Win64-Shipping.exe", size: 120 * 1024 * 1024);
        var candidates = new ExecutableDetector().Rank(temp.Path, "Example");
        Assert.Equal(shipping, candidates[0].Path); Assert.Equal(DetectionConfidence.High, candidates[0].Confidence);
    }

    [Fact]
    public void RecognizesUnityAndArchitecture()
    {
        using var temp = new TestDirectory(); var executable = temp.Pe("Unity Game.exe"); temp.File("UnityPlayer.dll", "marker"); temp.Directory("Unity Game_Data");
        var detector = new ExecutableDetector();
        Assert.Equal(GameEngine.Unity, detector.DetectEngine(temp.Path)); Assert.Equal(PeArchitecture.X64, detector.Rank(temp.Path, "Unity Game")[0].Architecture);
    }

    [Fact]
    public void PenalizesCrashAndBenchmarkTools()
    {
        using var temp = new TestDirectory(); var game = temp.Pe("Game.exe", size: 10 * 1024 * 1024);
        temp.Pe("GameBenchmark.exe", size: 200 * 1024 * 1024); temp.Pe("CrashReporter.exe", size: 300 * 1024 * 1024);
        Assert.Equal(game, new ExecutableDetector().Rank(temp.Path, "Game")[0].Path);
    }
}
