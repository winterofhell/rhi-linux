using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class ComponentDetectionTests
{
    [Fact]
    public async Task ValidWorkingOptiScalerLayoutIsInstalled()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallWorkingStackAsync(temp, game);

        var statuses = await new ComponentDetector().DetectAsync(game);

        Assert.All(statuses, status => Assert.Equal(ComponentHealth.Installed, status.Health));
    }

    [Fact]
    public async Task MetadataOnlyMismatchDoesNotRequireRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallWorkingStackAsync(temp, game);
        await File.AppendAllTextAsync(Path.Combine(game.DeploymentDirectory, "OptiScaler.ini"), "UserSetting=custom\n");

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.Installed, status.Health);
        Assert.Equal(InstallationVerification.MetadataUnverified, status.Verification);
        Assert.Contains("does not require repair", status.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecognizedProxyWithChangedManagedHashDoesNotRequireRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallWorkingStackAsync(temp, game);
        await File.AppendAllTextAsync(Path.Combine(game.DeploymentDirectory, "dxgi.dll"), "recognized OptiScaler build");

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.Installed, status.Health);
        Assert.Equal(InstallationVerification.MetadataUnverified, status.Verification);
    }

    [Fact]
    public async Task MissingRequiredProxyRequiresRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallWorkingStackAsync(temp, game);
        File.Delete(Path.Combine(game.DeploymentDirectory, "dxgi.dll"));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.PartiallyInstalled, status.Health);
        Assert.Equal(InstallationVerification.RepairNeeded, status.Verification);
    }

    [Fact]
    public async Task MissingReShadeChainingFileRequiresRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallWorkingStackAsync(temp, game);
        File.Delete(Path.Combine(game.DeploymentDirectory, "ReShade64.dll"));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.PartiallyInstalled, status.Health);
        Assert.Contains("chaining", status.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OptionalFileAbsenceDoesNotRequireRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallWorkingStackAsync(temp, game);
        var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest.Files.Add(new("plugins/OptiPatcher.asi", ComponentKind.OptiPatcher, new string('a', 64), "legacy", null, null));
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.Installed, status.Health);
        Assert.Equal(InstallationVerification.OptionalCleanupAvailable, status.Verification);
    }

    [Fact]
    public async Task LegacyOwnershipRecordIsMigratedForVerification()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallWorkingStackAsync(temp, game);
        var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest.SchemaVersion = 0;
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.Installed, status.Health);
        Assert.Equal(InstallationVerification.MetadataUnverified, status.Verification);
        Assert.Contains("migrated", status.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnershipManifestForAnotherAppIdIsUnknownInstallation()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, temp.Pe("stage/ReShade64.dll"), "ReShade64.dll", "6.7.3")), false)).Succeeded);
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-fixture.addon64"), "renodx-fixture.addon64", "1")), false)).Succeeded);
        var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest.AppId = 999;
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game), x => x.Component == ComponentKind.RenoDx);

        Assert.Equal(ComponentHealth.ForeignInstallation, status.Health);
        Assert.Contains("AppID 999", status.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenoDxArchitectureMismatchRequiresRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp) with
        {
            AppId = 1091500,
            Name = "Cyberpunk 2077",
            Candidates = [new(Path.Combine(temp.Path, "game", "Game.exe"), 100, DetectionConfidence.High,
                PeArchitecture.X64, 1024, ["fixture"])]
        };
        var installed = temp.Pe("game/renodx-wrong.addon32", 0x014c);
        temp.File("game/.rhi-linux/manifest.json", JsonSerializer.Serialize(new GameManifest
        {
            AppId = game.AppId,
            Files =
            [
                new("renodx-wrong.addon32", ComponentKind.RenoDx,
                    await ArtifactDownloader.Sha256Async(installed), "1", null, null)
            ]
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game), x => x.Component == ComponentKind.RenoDx);

        Assert.Equal(ComponentHealth.IncorrectlyConfigured, status.Health);
        Assert.Contains("architecture", status.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagedExactRenoDxProvenanceOverridesGenericProfileIdentityDuringRescan()
    {
        using var temp = new TestDirectory();
        var game = Game(temp) with
        {
            AppId = 424242,
            Name = "Remote Exact Game",
            Engine = GameEngine.Unity
        };
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, temp.Pe("stage/ReShade64.dll"), "ReShade64.dll", "6.7.3")), false)).Succeeded);
        var fileName = "renodx-remoteexact.addon64";
        var exact = new ComponentArtifact(ComponentKind.RenoDx, temp.Pe($"stage/{fileName}"), fileName,
            "snapshot", "https://author.github.io/renodx/renodx-remoteexact.addon64",
            RelativePath: fileName);
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, exact), false)).Succeeded);

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            component => component.Component == ComponentKind.RenoDx);

        Assert.Equal(ComponentHealth.Installed, status.Health);
    }

    [Fact]
    public async Task OptiScalerCoexistenceRequiresBothChainingSettings()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "1"),
            null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler, temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"), "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        await File.WriteAllTextAsync(Path.Combine(game.DeploymentDirectory, "OptiScaler.ini"),
            "[Plugins]\nLoadReshade=true\nLoadAsiPlugins=false\n");

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game), x => x.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.Installed, status.Health);
        Assert.Equal(InstallationVerification.MetadataUnverified, status.Verification);
        Assert.Contains("mutable configuration", status.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnershipPathOutsideGameIsRejected()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        temp.File("game/.rhi-linux/manifest.json", JsonSerializer.Serialize(new GameManifest
        {
            AppId = game.AppId,
            Files = [new("../outside.addon64", ComponentKind.RenoDx, "00", "1", null, null)]
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game), x => x.Component == ComponentKind.RenoDx);

        Assert.Equal(ComponentHealth.Broken, status.Health);
        Assert.Contains("outside", status.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptOwnershipManifestProducesUnknownStateInsteadOfStaleInstalledData()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        temp.File("game/.rhi-linux/manifest.json", "{not-json");

        var statuses = await new ComponentDetector().DetectAsync(game);

        Assert.All(statuses, status => Assert.Equal(ComponentHealth.ManifestUnavailable, status.Health));
        Assert.All(statuses, status => Assert.Contains("could not be verified", status.Explanation, StringComparison.Ordinal));
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new(42, "Fixture", temp.Path, temp.Path, root, temp.Combine("compatdata", "42", "pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]);
    }

    private static async Task InstallWorkingStackAsync(TestDirectory temp, SteamGame game)
    {
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade64.dll", "ReShade reshade.me"), "ReShade64.dll", "6.7.3"),
            new(ComponentKind.RenoDx, temp.PeWithMarker("stage/renodx-fixture.addon64", "RenoDX"), "renodx-fixture.addon64", "1"),
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);
        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);
        Assert.True((await new DeploymentExecutor().ExecuteAsync(plan, false)).Succeeded);
    }
}
