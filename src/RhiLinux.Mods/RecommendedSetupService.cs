using RhiLinux.Core;

namespace RhiLinux.Mods;

public interface IRecommendedSetupService
{
    RecommendedSetupResult Build(
        InstalledGame game,
        IReadOnlyList<ComponentStatus> components,
        ProxySelectionResult? proxy,
        bool preferExistingManagedVersions = true,
        bool warnBeforeAntiCheat = true);
}

public sealed class RecommendedSetupService : IRecommendedSetupService
{
    public const string RulesVersion = "1";

    public RecommendedSetupResult Build(
        InstalledGame game,
        IReadOnlyList<ComponentStatus> components,
        ProxySelectionResult? proxy,
        bool preferExistingManagedVersions = true,
        bool warnBeforeAntiCheat = true)
    {
        var reshade = components.FirstOrDefault(item => item.Component == ComponentKind.ReShade);
        var renodx = components.FirstOrDefault(item => item.Component == ComponentKind.RenoDx);
        var opti = components.FirstOrDefault(item => item.Component == ComponentKind.OptiScaler);
        var options = new List<RecommendedSetupOption>();
        var foreign = components.Any(item => item.Health == ComponentHealth.ForeignInstallation) ||
            proxy?.Candidates.Any(candidate =>
                candidate.Classification is ProxyFileClassification.UnknownDll or ProxyFileClassification.KnownThirdPartyInjector &&
                !candidate.SafeForNewInstallation) == true;

        if (foreign)
        {
            options.Add(new(
                "foreign-block",
                "Foreign compatibility file detected",
                "RHI will not replace an unknown proxy DLL automatically. Inspect Advanced details or remove the foreign file first.",
                false,
                true,
                "ForeignProxyConflict"));
            return new("Foreign dxgi.dll or unknown proxy detected. RHI will not replace this file automatically.", options, "foreign-block");
        }

        if (game.IsNativeLinux || game.Platform == GameBinaryPlatform.Linux)
        {
            options.Add(new(
                "native-unsupported",
                "Native Linux game",
                "Windows proxy deployment is not supported for native Linux binaries.",
                false,
                true,
                "NativeGameUnsupported"));
            return new("Native Linux game. Deployment is unsupported.", options, "native-unsupported");
        }

        if (game.RequiresConfirmation && warnBeforeAntiCheat)
        {
            options.Add(new(
                "anticheat-confirm",
                "Anti-cheat confirmation required",
                "Anti-cheat files were detected. Deployment requires explicit confirmation and may prevent online play.",
                true,
                false));
        }

        var reshadeInstalled = IsInstalled(reshade);
        var renodxInstalled = IsInstalled(renodx);
        var optiInstalled = IsInstalled(opti);
        var repairNeeded = components.Any(item => item.Health is ComponentHealth.Broken or
            ComponentHealth.PartiallyInstalled or ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable);

        if (repairNeeded)
            options.Add(new("repair-managed", "Repair managed setup",
                "Rebuild the supported managed files and configuration while preserving user-owned files.", true, true));

        if (!reshadeInstalled && !renodxInstalled && !optiInstalled)
        {
            options.Add(new("install-recommended", "Install recommended stack",
                "Install the supported official ReShade, RenoDX, and OptiScaler combination for this game.", true, true));
            if (reshade?.Health is ComponentHealth.Available or ComponentHealth.Supported or ComponentHealth.DownloadRequired or ComponentHealth.Cached)
                options.Add(new("install-reshade", "Install ReShade only", "Install managed ReShade without RenoDX or OptiScaler.", true, false));
        }
        else if (reshadeInstalled && !renodxInstalled && !optiInstalled)
        {
            if (preferExistingManagedVersions && reshade?.Health != ComponentHealth.Outdated)
                options.Add(new("keep-reshade", "Keep current ReShade", "Leave the installed managed ReShade unchanged.", true, false));
            if (reshade?.Health == ComponentHealth.Outdated)
                options.Add(new("update-reshade", "Update ReShade", "Update the managed ReShade installation from the official source.", true, true));
            if (IsActionable(renodx))
                options.Add(new("add-renodx", "Add RenoDX", "Install the official RenoDX addon alongside the current ReShade proxy.", true, reshade?.Health != ComponentHealth.Outdated));
            if (IsActionable(opti))
                options.Add(new("add-optiscaler", "Add OptiScaler with coexistence",
                    "Rename ReShade to ReShade64.dll, install OptiScaler as the proxy, and enable LoadReshade.", true, false));
        }
        else if (reshadeInstalled && renodxInstalled && !optiInstalled)
        {
            if (IsActionable(opti))
                options.Add(new("add-optiscaler-coexist", "Add OptiScaler with coexistence",
                    "Apply the supported ReShade rename and OptiScaler coexistence configuration.", true, true));
            if (renodx?.Health == ComponentHealth.Outdated)
                options.Add(new("update-renodx", "Update RenoDX", "Update the managed RenoDX addon from the official source.", true, false));
            if (preferExistingManagedVersions)
                options.Add(new("keep-current", "Keep current managed stack", "Leave ReShade and RenoDX unchanged.", true, false));
        }
        else if (optiInstalled || (reshadeInstalled && renodxInstalled && optiInstalled))
        {
            if (components.Any(item => item.Health == ComponentHealth.Outdated))
                options.Add(new("update-outdated", "Update outdated components", "Update managed components that have newer official releases.", true, true));
            else
                options.Add(new("up-to-date", "Stack is up to date", "Installed managed components match the known official sources.", true, true));
        }

        if (options.Count == 0)
            options.Add(new("inspect", "Inspect current detection", "Open readiness details and Advanced compatibility information.", true, true));

        var primary = options.FirstOrDefault(option => option is { IsPrimary: true, IsSupported: true }) ??
            options.FirstOrDefault(option => option.IsSupported) ??
            options[0];
        var summary = primary.IsSupported
            ? primary.Title + ". " + primary.Explanation
            : primary.UnsupportedReason ?? primary.Explanation;
        return new(summary, options, primary.Id);
    }

    private static bool IsInstalled(ComponentStatus? status) =>
        status?.Health is ComponentHealth.Installed or ComponentHealth.Outdated or ComponentHealth.Broken
            or ComponentHealth.PartiallyInstalled or ComponentHealth.IncorrectlyConfigured or ComponentHealth.RepairAvailable;

    private static bool IsActionable(ComponentStatus? status) =>
        status?.Health is ComponentHealth.Available or ComponentHealth.Supported or ComponentHealth.DownloadRequired
            or ComponentHealth.Cached or ComponentHealth.Outdated or ComponentHealth.Experimental;
}
