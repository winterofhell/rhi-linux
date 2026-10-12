using RhiLinux.Core;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class SteamArtworkTests
{
    private const uint AppId = 3999999901;

    [Fact]
    public void FindsNamedArtworkInsideAppDirectory()
    {
        using var temp = new TestDirectory();
        var hero = temp.File($"appcache/librarycache/{AppId}/library_hero.jpg", "hero");
        temp.File($"appcache/librarycache/{AppId}/header.jpg", "header");

        Assert.Equal(hero, SteamArtworkLocator.Find(temp.Path, AppId, SteamArtworkKind.Hero));
    }

    [Fact]
    public void HeroFallsBackToHeaderAndHeaderFallsBackToHero()
    {
        using var temp = new TestDirectory();
        var header = temp.File($"appcache/librarycache/{AppId}/header.jpg", "header");

        Assert.Equal(header, SteamArtworkLocator.Find(temp.Path, AppId, SteamArtworkKind.Hero));
    }

    [Fact]
    public void FallsBackToLargestHashedImageAndIgnoresBlurAndLogo()
    {
        using var temp = new TestDirectory();
        temp.File($"appcache/librarycache/{AppId}/library_hero_blur.jpg", new string('b', 4096));
        temp.File($"appcache/librarycache/{AppId}/logo.png", new string('l', 4096));
        temp.File($"appcache/librarycache/{AppId}/1111111111111111111111111111111111111111.jpg", "small");
        var large = temp.File($"appcache/librarycache/{AppId}/2222222222222222222222222222222222222222.jpg", new string('x', 2048));

        Assert.Equal(large, SteamArtworkLocator.Find(temp.Path, AppId, SteamArtworkKind.Header));
    }

    [Fact]
    public void IgnoresEmptyFilesAndMissingApps()
    {
        using var temp = new TestDirectory();
        temp.File($"appcache/librarycache/{AppId}/header.jpg", string.Empty);

        Assert.Null(SteamArtworkLocator.Find(temp.Path, AppId, SteamArtworkKind.Logo));
        Assert.Null(SteamArtworkLocator.Find(temp.Path, 0, SteamArtworkKind.Header));
    }

    [Fact]
    public void ReconciledSteamGamesKeepSteamAndLibraryRoots()
    {
        var record = new SourceGameRecord(
            "steam", GameStore.Steam, GameLauncher.Steam, "4242", "Demo",
            "/library/steamapps/common/Demo", "/library/steamapps/common/Demo/Demo.exe", null, null,
            GameBinaryPlatform.Windows, CompatibilityEnvironment.Proton,
            "/steam/steamapps/appmanifest_4242.acf", null, DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["appId"] = "4242", ["steamRoot"] = "/steam", ["libraryRoot"] = "/library" }, []);

        var game = Assert.Single(GameReconciliation.Reconcile([record]).Games);

        Assert.Equal("/steam", game.SteamRoot);
        Assert.Equal("/library", game.LibraryRoot);
    }
}
