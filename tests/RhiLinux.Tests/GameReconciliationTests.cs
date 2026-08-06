using RhiLinux.Core;

namespace RhiLinux.Tests;

public sealed class GameReconciliationTests
{
    [Fact]
    public void ReconcileIsOrderIndependentForDuplicatePhysicalInstalls()
    {
        var steam = Record(
            "steam",
            GameStore.Steam,
            GameLauncher.Steam,
            "100",
            "/games/Control",
            "/games/Control/Control.exe",
            "/prefixes/control",
            "a.json");
        var heroic = Record(
            "heroic",
            GameStore.Epic,
            GameLauncher.Heroic,
            "Control",
            "/games/Control",
            "/games/Control/Control.exe",
            "/prefixes/control",
            "b.json");
        var lutris = Record(
            "lutris",
            GameStore.Other,
            GameLauncher.Lutris,
            "control-wine",
            "/games/Control",
            "/games/Control/Control.exe",
            "/prefixes/control",
            "c.json");

        var forward = GameReconciliation.Reconcile([steam, heroic, lutris]);
        var shuffled = GameReconciliation.Reconcile([lutris, steam, heroic]);
        var reversed = GameReconciliation.Reconcile([heroic, lutris, steam]);

        var forwardGame = Assert.Single(forward.Games);
        Assert.Equal(forward.Games.Select(game => game.InstallId.Value),
            shuffled.Games.Select(game => game.InstallId.Value));
        Assert.Equal(forward.Games.Select(game => game.InstallId.Value),
            reversed.Games.Select(game => game.InstallId.Value));
        Assert.Equal(forward.DuplicatePhysicalInstallCount, shuffled.DuplicatePhysicalInstallCount);
        Assert.Equal(forward.DuplicatePhysicalInstallCount, reversed.DuplicatePhysicalInstallCount);
        Assert.True(forward.DuplicatePhysicalInstallCount >= 1);
        Assert.Equal(GameLauncher.Steam, forwardGame.PrimaryLauncher);
        Assert.Equal(3, forwardGame.Sources.Count);
    }

    private static SourceGameRecord Record(
        string providerId,
        GameStore store,
        GameLauncher launcher,
        string externalId,
        string installRoot,
        string executable,
        string prefix,
        string metadataPath) =>
        new(
            providerId,
            store,
            launcher,
            externalId,
            "Control",
            installRoot,
            executable,
            prefix,
            installRoot,
            GameBinaryPlatform.Windows,
            CompatibilityEnvironment.Proton,
            metadataPath,
            null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(),
            []);
}
