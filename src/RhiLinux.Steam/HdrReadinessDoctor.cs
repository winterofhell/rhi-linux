using RhiLinux.Core;

namespace RhiLinux.Steam;

public enum HdrReadinessStatus
{
    Ready,
    ReadyWithManualStep,
    MissingGamescope,
    DesktopHdrUnavailable,
    DriverCapabilityUnknown,
    UnsupportedSession
}

public sealed record HdrReadinessReport(
    HdrReadinessStatus Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> RecommendedVariables);

public static class HdrReadinessDoctor
{
    public static HdrReadinessReport Evaluate(PlatformCapabilities capabilities)
    {
        var evidence = new List<string>
        {
            $"Session type: {capabilities.SessionType}",
            $"Desktop: {capabilities.DesktopEnvironment ?? "unknown"}",
            $"GPU vendor: {capabilities.GpuVendor}",
            capabilities.HasGamescope ? $"Gamescope: {capabilities.GamescopePath}" : "Gamescope: not found",
            capabilities.HasVulkan ? "Vulkan tools: present" : "Vulkan tools: not detected"
        };

        if (capabilities.IsSteamDeck)
            evidence.Add("Steam Deck markers detected.");

        if (capabilities.SessionType.Equals("wayland", StringComparison.OrdinalIgnoreCase) &&
            capabilities.HasGamescope &&
            capabilities.GpuVendor is "AMD" or "Unknown")
        {
            return new HdrReadinessReport(
                HdrReadinessStatus.ReadyWithManualStep,
                "HDR may work with Gamescope and Proton Wayland variables after display HDR is enabled.",
                evidence,
                [SteamLaunchOptionService.ProtonEnableWayland, SteamLaunchOptionService.DxvkHdr]);
        }

        if (!capabilities.HasGamescope)
        {
            return new HdrReadinessReport(
                HdrReadinessStatus.MissingGamescope,
                "Gamescope was not found. Install gamescope before using HDR launch presets.",
                evidence,
                [SteamLaunchOptionService.ProtonEnableWayland, SteamLaunchOptionService.DxvkHdr]);
        }

        if (capabilities.SessionType.Equals("x11", StringComparison.OrdinalIgnoreCase))
        {
            return new HdrReadinessReport(
                HdrReadinessStatus.UnsupportedSession,
                "Current session is X11. Prefer a Wayland session or Gamescope nested HDR.",
                evidence,
                []);
        }

        if (capabilities.GpuVendor is "NVIDIA")
        {
            return new HdrReadinessReport(
                HdrReadinessStatus.DesktopHdrUnavailable,
                "This build targets AMD workflows. NVIDIA HDR support is not managed here.",
                evidence,
                []);
        }

        return new HdrReadinessReport(
            HdrReadinessStatus.DriverCapabilityUnknown,
            "HDR capability could not be confirmed from local evidence alone.",
            evidence,
            [SteamLaunchOptionService.ProtonEnableWayland, SteamLaunchOptionService.DxvkHdr]);
    }
}
