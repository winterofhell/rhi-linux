namespace RhiLinux.Core;

public sealed record PlatformCapabilities(
    string SessionType,
    string? DesktopEnvironment,
    bool IsFlatpak,
    bool IsSteamFlatpak,
    bool HasGamescope,
    bool IsSteamDeck,
    bool HasGameMode,
    bool HasMangoHud,
    bool HasVkBasalt,
    bool HasVulkan,
    string GpuVendor,
    bool NativeFilesystemAccess,
    bool DesktopNotifications,
    bool TraySupport,
    bool PortalSupport,
    string GamescopePath,
    string GameModePath,
    string MangoHudPath,
    IReadOnlyList<string> Notes);

public interface IPlatformCapabilityProvider
{
    PlatformCapabilities Detect();
}

public sealed class LinuxPlatformCapabilityProvider : IPlatformCapabilityProvider
{
    public PlatformCapabilities Detect()
    {
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
            ?? Environment.GetEnvironmentVariable("DESKTOP_SESSION");
        var flatpak = File.Exists("/.flatpak-info") ||
                      !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLATPAK_ID"));
        var steamFlatpakId = Environment.GetEnvironmentVariable("FLATPAK_ID");
        var isSteamFlatpak = string.Equals(steamFlatpakId, "com.valvesoftware.Steam", StringComparison.Ordinal);
        var gamescope = FindOnPath("gamescope");
        var gamemode = FindOnPath("gamemoderun") ?? FindOnPath("gamemode");
        var mangohud = FindOnPath("mangohud");
        var vkbasalt = FindOnPath("vkbasalt") is not null ||
                       File.Exists("/usr/lib/libvkbasalt.so") ||
                       File.Exists("/usr/lib64/libvkbasalt.so");
        var steamDeck = string.Equals(Environment.GetEnvironmentVariable("SteamDeck"), "1", StringComparison.Ordinal) ||
                        File.Exists("/etc/steamos-release");
        var gpu = DetectGpuVendor();
        var notes = new List<string>();
        if (gamescope is null) notes.Add("Gamescope was not found on PATH.");
        if (gamemode is null) notes.Add("GameMode was not found on PATH.");
        if (mangohud is null) notes.Add("MangoHud was not found on PATH.");
        if (steamDeck) notes.Add("Steam Deck environment markers were detected.");

        return new PlatformCapabilities(
            session,
            desktop,
            flatpak,
            isSteamFlatpak,
            gamescope is not null,
            steamDeck,
            gamemode is not null,
            mangohud is not null,
            vkbasalt,
            File.Exists("/usr/bin/vulkaninfo") || FindOnPath("vulkaninfo") is not null,
            gpu,
            !flatpak,
            FindOnPath("notify-send") is not null,
            desktop?.Contains("KDE", StringComparison.OrdinalIgnoreCase) == true ||
            desktop?.Contains("GNOME", StringComparison.OrdinalIgnoreCase) == true,
            flatpak || FindOnPath("xdg-desktop-portal") is not null,
            gamescope ?? string.Empty,
            gamemode ?? string.Empty,
            mangohud ?? string.Empty,
            notes);
    }

    private static string DetectGpuVendor()
    {
        try
        {
            if (Directory.Exists("/sys/class/drm"))
            {
                foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
                {
                    var vendorPath = Path.Combine(card, "device", "vendor");
                    if (!File.Exists(vendorPath)) continue;
                    var vendor = File.ReadAllText(vendorPath).Trim();
                    if (vendor.Equals("0x1002", StringComparison.OrdinalIgnoreCase)) return "AMD";
                    if (vendor.Equals("0x10de", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
                    if (vendor.Equals("0x8086", StringComparison.OrdinalIgnoreCase)) return "Intel";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return "Unknown";
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
