using System.Security.Cryptography;
using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class OptiScalerLayoutDetectionTests
{
    [Fact]
    public async Task WorkingStackWithLateOptiScalerMarkerIsInstalledNotRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");

        var snapshot = await new ComponentDetector().DetectStackAsync(game);
        var opti = Assert.Single(snapshot.Components, x => x.Component == ComponentKind.OptiScaler);
        var card = new ComponentCardViewModel(StackDetector.ToComponentStatus(opti));

        Assert.Equal("version.dll", snapshot.ActiveProxy);
        Assert.Equal(ComponentKind.OptiScaler, snapshot.ProxyOwner);
        Assert.Equal(ComponentLifecycleState.InstalledHealthy, opti.State);
        Assert.False(card.CanRepair);
        Assert.Equal("Installed", card.State);
        Assert.DoesNotContain("proxy DLL is missing", opti.Evidence.RepairReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlternativeValidProxyFilenameIsAccepted()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "winmm.dll");

        var snapshot = await new ComponentDetector().DetectStackAsync(game);

        Assert.Equal("winmm.dll", snapshot.ActiveProxy);
        Assert.Equal(ComponentLifecycleState.InstalledHealthy,
            Assert.Single(snapshot.Components, x => x.Component == ComponentKind.OptiScaler).State);
    }

    [Fact]
    public async Task ActiveProxyDerivedFromPreferredConfiguration()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "d3d12.dll");
        File.Copy(
            Path.Combine(game.DeploymentDirectory, "d3d12.dll"),
            Path.Combine(game.DeploymentDirectory, "version.dll"));

        var snapshot = await new ComponentDetector().DetectStackAsync(game);

        Assert.Equal("d3d12.dll", snapshot.ActiveProxy);
        Assert.Contains(snapshot.Components.Single(x => x.Component == ComponentKind.OptiScaler).Evidence.ExpectedFiles,
            name => name.Equals("version.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FullReShadeRenoDxOptiScalerStackIsHealthy()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");

        var snapshot = await new ComponentDetector().DetectStackAsync(game);

        Assert.Equal(StackLayoutKind.FullStack, snapshot.Layout);
        Assert.True(snapshot.IsHealthy);
        Assert.True(snapshot.ChainingConfigured);
        Assert.All(snapshot.Components, component =>
            Assert.Equal(ComponentLifecycleState.InstalledHealthy, component.State));
    }

    [Fact]
    public async Task OldOwnershipPathDoesNotForceRepairWhenAlternateProxyIsHealthy()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");
        var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest.Files.Add(new("dxgi.dll", ComponentKind.OptiScaler, new string('b', 64), "v0.9.4", null, null,
            FileClass: ManagedFileClass.ImmutableRuntimeBinary, BundleRelativePath: "OptiScaler.dll"));
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.Installed, status.Health);
        Assert.NotEqual(InstallationVerification.RepairNeeded, status.Verification);
    }

    [Fact]
    public async Task OptionalFileAbsenceDoesNotRequireRepairForAlternateProxy()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");
        var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest.Files.Add(new("plugins/OptiPatcher.asi", ComponentKind.OptiPatcher, new string('a', 64), "legacy", null, null));
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.Installed, status.Health);
        Assert.False(new ComponentCardViewModel(status).CanRepair);
    }

    [Fact]
    public async Task RequiredCoreProxyGenuinelyAbsentRequiresRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");
        File.Delete(Path.Combine(game.DeploymentDirectory, "version.dll"));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(InstallationVerification.RepairNeeded, status.Verification);
        Assert.Contains("proxy", status.RepairReason ?? status.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepairOnHealthyStackPerformsNoWrites()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");
        var before = await SnapshotHashesAsync(game.DeploymentDirectory);

        var repair = await new DeploymentPlanner().BuildRepairPlanAsync(game, ComponentKind.OptiScaler,
            new(ComponentKind.OptiScaler, temp.PeWithLateMarker("stage/repair/OptiScaler.dll", "OptiScaler"),
                "OptiScaler.dll", "v0.9.4"));
        Assert.False(repair.RequiresRepair);
        Assert.Empty(repair.Operations);
        Assert.Contains("No repair needed", repair.CompatibilityMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True((await new DeploymentExecutor().ExecuteAsync(repair, false)).Succeeded);

        foreach (var (path, hash) in before)
            Assert.Equal(hash, await ArtifactDownloader.Sha256Async(path));
    }

    [Fact]
    public async Task RepairPlanUsesDetectedProxyLayout()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");
        File.Delete(Path.Combine(game.DeploymentDirectory, "version.dll"));
        var artifact = new ComponentArtifact(ComponentKind.OptiScaler,
            temp.PeWithLateMarker("stage/repair/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4");

        var plan = await new DeploymentPlanner().BuildRepairPlanAsync(game, ComponentKind.OptiScaler, artifact);

        Assert.True(plan.RequiresRepair);
        Assert.Contains(plan.Operations, operation =>
            operation.Type == DeploymentOperationType.Copy &&
            operation.Target.EndsWith("version.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Operations, operation =>
            operation.Type == DeploymentOperationType.VerifyFileState &&
            operation.Target.EndsWith("dxgi.dll", StringComparison.OrdinalIgnoreCase) &&
            (operation.Value ?? "exists").Equals("exists", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RefreshRemovesFalseRepairAfterLateMarkerRecognition()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");

        var first = await new ComponentDetector().DetectStackAsync(game);
        var second = await new ComponentDetector().DetectStackAsync(game with { });

        Assert.Equal(first.ActiveProxy, second.ActiveProxy);
        Assert.All(second.Components, component => Assert.NotEqual(ComponentLifecycleState.RepairRequired, component.State));
        Assert.Empty(second.ConcreteDefects ?? []);
    }

    [Fact]
    public async Task StaleMissingProxyDefectIsNotPreservedAfterHealthyScan()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallFullStackWithLateProxyAsync(temp, game, "version.dll");
        var healthy = await new ComponentDetector().DetectStackAsync(game);
        Assert.True(healthy.IsHealthy);

        var card = new ComponentCardViewModel(StackDetector.ToComponentStatus(
            healthy.Components.Single(x => x.Component == ComponentKind.OptiScaler)));
        Assert.False(card.CanRepair);
        Assert.Null(card.RepairReason);
    }

    [Fact]
    public void BinaryMarkerScannerFindsMarkerBeyondTwoMegabytes()
    {
        using var temp = new TestDirectory();
        var path = temp.PeWithLateMarker("late.dll", "OptiScaler", 5L * 1024 * 1024);
        Assert.True(BinaryMarkerScanner.ContainsAny(path, "OptiScaler"));
        Assert.False(BinaryMarkerScanner.ContainsAny(path, "MissingMarker"));
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var deploy = temp.Directory("game/bin/x64");
        var executable = temp.Pe("game/bin/x64/Game.exe");
        return new(1091500, "Fixture", temp.Path, temp.Path, root,
            temp.Combine("compatdata", "1091500", "pfx"), executable, deploy, DetectionConfidence.High,
            "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]);
    }

    private static async Task InstallFullStackWithLateProxyAsync(TestDirectory temp, SteamGame game, string proxyName)
    {
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var reshade = temp.PeWithLateMarker("stage/ReShade.dll", "reshade.me", 3L * 1024 * 1024);
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, reshade, "ReShade.dll", "6.7.3"), "dxgi.dll"), false)).Succeeded);
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-cp2077.addon64"), "renodx-cp2077.addon64", "snapshot")),
            false)).Succeeded);

        var opti = temp.PeWithLateMarker("stage/OptiScaler.dll", "OptiScaler", 3L * 1024 * 1024);
        var migrate = await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.OptiScaler, opti, "OptiScaler.dll", "v0.9.4"), "dxgi.dll");
        Assert.True((await executor.ExecuteAsync(migrate, false)).Succeeded);

        var currentProxy = Path.Combine(game.DeploymentDirectory, "dxgi.dll");
        var targetProxy = Path.Combine(game.DeploymentDirectory, proxyName);
        if (!proxyName.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase))
        {
            File.Move(currentProxy, targetProxy, true);
            var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
            var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            foreach (var file in manifest.Files.Where(x =>
                         Path.GetFileName(x.RelativePath).Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                manifest.Files.Remove(file);
                var relative = Path.GetRelativePath(game.GameRoot, targetProxy);
                manifest.Files.Add(file with
                {
                    RelativePath = relative,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(targetProxy))).ToLowerInvariant()
                });
            }

            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        await File.WriteAllTextAsync(Path.Combine(game.DeploymentDirectory, "OptiScaler.ini"),
            "[Plugins]\nLoadReshade=true\nLoadAsiPlugins=true\n");
    }

    private static async Task<Dictionary<string, string>> SnapshotHashesAsync(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result[path] = await ArtifactDownloader.Sha256Async(path);
        return result;
    }
}
