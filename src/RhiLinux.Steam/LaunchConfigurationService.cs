using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed class LaunchConfigurationService : ILaunchConfigurationProvider
{
    public async Task<LaunchConfigurationResult> GetAsync(
        InstalledGame game,
        string? requiredProxyDll,
        string? requiredLaunchOption,
        CancellationToken cancellationToken = default)
    {
        var proxy = string.IsNullOrWhiteSpace(requiredProxyDll) ? "dxgi.dll" : requiredProxyDll;
        var required = string.IsNullOrWhiteSpace(requiredLaunchOption)
            ? $"WINEDLLOVERRIDES=\"{Path.GetFileNameWithoutExtension(proxy)}=n,b\" %command%"
            : requiredLaunchOption.Trim();
        var wineAssignment = ExtractWineAssignment(required, proxy);

        return game.Launcher switch
        {
            GameLauncher.Steam => await ForSteamAsync(game, required, cancellationToken).ConfigureAwait(false),
            GameLauncher.Heroic => ForHeroic(wineAssignment),
            GameLauncher.Lutris => ForLutris(wineAssignment),
            GameLauncher.Bottles => ForBottles(wineAssignment, game),
            GameLauncher.Legendary => ForManualOnly(GameLauncher.Legendary, wineAssignment,
                "Automatic Legendary config editing is not implemented. Add the environment variable to the game's launch options manually."),
            GameLauncher.Minigalaxy => ForManualOnly(GameLauncher.Minigalaxy, wineAssignment,
                "Automatic Minigalaxy config editing is not implemented. Add the environment variable before launching the game."),
            GameLauncher.Manual => ForManualOnly(GameLauncher.Manual, wineAssignment,
                "Add this environment variable to the custom launcher or shell script that starts the game."),
            _ => ForManualOnly(game.Launcher, wineAssignment,
                "Copy this environment variable into the launcher that starts the game.")
        };
    }

    private static async Task<LaunchConfigurationResult> ForSteamAsync(
        InstalledGame game,
        string required,
        CancellationToken cancellationToken)
    {
        var detected = await SteamLaunchOptionService.TryReadLaunchOptionsAsync(game, cancellationToken)
            .ConfigureAwait(false);
        var observation = SteamLaunchOptionService.Observe(game, required, detected);
        return new(
            GameLauncher.Steam,
            required,
            required,
            "Paste this value into the game's Steam launch options.",
            observation.Status,
            observation.Explanation,
            ManualCopyRequired: observation.Status is not LaunchOptionStatus.Correct and not LaunchOptionStatus.NotRequired,
            AutomaticEditingSupported: false);
    }

    private static LaunchConfigurationResult ForHeroic(string wineAssignment) => new(
        GameLauncher.Heroic,
        wineAssignment,
        wineAssignment,
        "Open Heroic → game settings → Advanced → Environment Variables, then add WINEDLLOVERRIDES with the value shown after the equals sign. Leave %command% out of Heroic environment fields.",
        LaunchOptionStatus.Missing,
        "Heroic requires a manual environment variable. Automatic editing is not implemented.",
        ManualCopyRequired: true,
        AutomaticEditingSupported: false);

    private static LaunchConfigurationResult ForLutris(string wineAssignment) => new(
        GameLauncher.Lutris,
        wineAssignment,
        wineAssignment,
        "Open the game in Lutris → Configure → System options → Environment variables, then add WINEDLLOVERRIDES.",
        LaunchOptionStatus.Missing,
        "Lutris requires a manual environment variable. Automatic editing is not implemented.",
        ManualCopyRequired: true,
        AutomaticEditingSupported: false);

    private static LaunchConfigurationResult ForBottles(string wineAssignment, InstalledGame game) => new(
        GameLauncher.Bottles,
        wineAssignment,
        wineAssignment,
        string.IsNullOrWhiteSpace(game.Name)
            ? "Open the bottle's program settings in Bottles and add WINEDLLOVERRIDES as an environment variable."
            : $"Open Bottles, select the bottle that runs “{game.Name}”, then add WINEDLLOVERRIDES as an environment variable for that program.",
        LaunchOptionStatus.Missing,
        "Bottles requires a manual environment variable. Automatic editing is not implemented.",
        ManualCopyRequired: true,
        AutomaticEditingSupported: false);

    private static LaunchConfigurationResult ForManualOnly(
        GameLauncher launcher,
        string wineAssignment,
        string instructions) => new(
        launcher,
        wineAssignment,
        wineAssignment,
        instructions,
        LaunchOptionStatus.Missing,
        "Manual launcher configuration is required.",
        ManualCopyRequired: true,
        AutomaticEditingSupported: false);

    private static string ExtractWineAssignment(string required, string proxy)
    {
        foreach (var token in required.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("WINEDLLOVERRIDES=", StringComparison.OrdinalIgnoreCase))
                return token;
        }

        return $"WINEDLLOVERRIDES=\"{Path.GetFileNameWithoutExtension(proxy)}=n,b\"";
    }
}
