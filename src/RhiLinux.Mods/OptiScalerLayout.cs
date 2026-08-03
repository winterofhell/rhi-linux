using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum OptiScalerLayoutKind
{
    OptiScalerOnly,
    OptiScalerReShade,
    OptiScalerRenoDx,
    OptiScalerReShadeRenoDx
}

public sealed record OptiScalerLayoutRequirement(
    OptiScalerLayoutKind Kind,
    IReadOnlyList<string> AcceptedActiveProxies,
    IReadOnlyList<string> RequiredCoreFiles,
    IReadOnlyList<string> ModeSpecificRequiredFiles,
    IReadOnlyList<string> OptionalFiles,
    string? ReShadeChainTarget,
    string? RenoDxHostPath,
    IReadOnlyList<string> RequiredIniKeys,
    string? LaunchOptionProxy,
    PeArchitecture Architecture,
    string DeploymentDirectory);

public sealed record OptiScalerLayoutMatch(
    OptiScalerLayoutRequirement Layout,
    string ActiveProxy,
    string ActiveProxyPath,
    string? OptiScalerCorePath,
    string? ReShadeChainPath,
    string? RenoDxPath,
    bool Healthy,
    string Explanation,
    IReadOnlyList<string> AcceptedProxyCandidates,
    string? RejectedGuessedProxy,
    string? RejectedGuessReason);

public static class OptiScalerLayoutCatalog
{
    public static readonly string[] AcceptedProxyNames = DeploymentPlanner.SupportedProxyNames;

    public static OptiScalerLayoutRequirement Create(
        OptiScalerLayoutKind kind,
        string deploymentDirectory,
        PeArchitecture architecture,
        string activeProxy,
        string? renoDxHostPath = null) =>
        kind switch
        {
            OptiScalerLayoutKind.OptiScalerOnly => new(
                kind, AcceptedProxyNames, ["OptiScaler runtime proxy"], [],
                ["OptiScaler.ini", "fakenvapi.dll", "amd_fidelityfx_dx12.dll"],
                null, null, [], activeProxy, architecture, deploymentDirectory),
            OptiScalerLayoutKind.OptiScalerReShade => new(
                kind, AcceptedProxyNames, ["OptiScaler runtime proxy"], ["ReShade64.dll"],
                ["OptiScaler.ini", "fakenvapi.dll"],
                "ReShade64.dll", null,
                ["LoadReshade=true", "LoadAsiPlugins=true"],
                activeProxy, architecture, deploymentDirectory),
            OptiScalerLayoutKind.OptiScalerRenoDx => new(
                kind, AcceptedProxyNames, ["OptiScaler runtime proxy"], ["ReShade64.dll", "RenoDX addon"],
                ["OptiScaler.ini"],
                "ReShade64.dll", renoDxHostPath,
                ["LoadReshade=true", "LoadAsiPlugins=true"],
                activeProxy, architecture, deploymentDirectory),
            OptiScalerLayoutKind.OptiScalerReShadeRenoDx => new(
                kind, AcceptedProxyNames, ["OptiScaler runtime proxy"], ["ReShade64.dll", "RenoDX addon"],
                ["OptiScaler.ini", "fakenvapi.dll"],
                "ReShade64.dll", renoDxHostPath,
                ["LoadReshade=true", "LoadAsiPlugins=true"],
                activeProxy, architecture, deploymentDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    public static OptiScalerLayoutKind DetermineKind(bool hasReShade, bool hasRenoDx) =>
        hasReShade && hasRenoDx ? OptiScalerLayoutKind.OptiScalerReShadeRenoDx :
        hasReShade ? OptiScalerLayoutKind.OptiScalerReShade :
        hasRenoDx ? OptiScalerLayoutKind.OptiScalerRenoDx :
        OptiScalerLayoutKind.OptiScalerOnly;
}
