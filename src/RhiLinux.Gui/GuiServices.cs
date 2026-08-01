using RhiLinux.Core;
using RhiLinux.Mods;
using RhiLinux.Steam;

namespace RhiLinux.Gui;

public interface IGameDiscovery
{
    Task<ScanResult> ScanAsync(IReadOnlyDictionary<uint, GameOverride> overrides, CancellationToken cancellationToken);
}

public interface IComponentStatusProvider
{
    Task<IReadOnlyList<ComponentStatus>> DetectAsync(SteamGame game, CancellationToken cancellationToken);
}

public interface IStackStatusProvider
{
    Task<StackStatusReport> GetAsync(
        SteamGame game,
        bool allowNetwork,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}

public sealed class GameDiscoveryAdapter(SteamDiscoveryService service, IReadOnlyList<string>? steamRoots = null) : IGameDiscovery
{
    public Task<ScanResult> ScanAsync(IReadOnlyDictionary<uint, GameOverride> overrides, CancellationToken cancellationToken) =>
        service.ScanAsync(steamRoots, overrides, cancellationToken);
}

public sealed class ComponentStatusProviderAdapter(ComponentDetector detector) : IComponentStatusProvider
{
    public Task<IReadOnlyList<ComponentStatus>> DetectAsync(SteamGame game, CancellationToken cancellationToken) =>
        detector.DetectAsync(game, cancellationToken);
}

public sealed class StackStatusProviderAdapter(StackStatusService service) : IComponentStatusProvider
{
    public async Task<IReadOnlyList<ComponentStatus>> DetectAsync(SteamGame game, CancellationToken cancellationToken) =>
        (await service.GetAsync(game, false, cancellationToken)).Components;
}

public sealed class StackReportProviderAdapter(StackStatusService service) : IStackStatusProvider
{
    public Task<StackStatusReport> GetAsync(
        SteamGame game,
        bool allowNetwork,
        CancellationToken cancellationToken,
        bool forceRefresh = false) => service.GetAsync(game, allowNetwork, cancellationToken, forceRefresh);
}
