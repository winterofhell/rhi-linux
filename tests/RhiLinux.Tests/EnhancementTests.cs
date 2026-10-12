using System.IO.Compression;
using System.Net;
using System.Text;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class EnhancementTests
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ShaderEntriesMapIntoPackFoldersAndSkipBrokenShaders()
    {
        Assert.Equal(Path.Combine("Shaders", "Pack", "Bloom.fx"), ShaderArchiveStager.MapEntry("repo-main/Shaders/Bloom.fx", "Pack"));
        Assert.Equal(Path.Combine("Textures", "Pack", "lut.png"), ShaderArchiveStager.MapEntry("Textures/lut.png", "Pack"));
        Assert.Equal(Path.Combine("Shaders", "Pack", "Root.fxh"), ShaderArchiveStager.MapEntry("repo-main/Root.fxh", "Pack"));
        Assert.Null(ShaderArchiveStager.MapEntry("repo-main/README.md", "Pack"));
        Assert.Null(ShaderArchiveStager.MapEntry("repo-main/Shaders/NTSC_XOT.fx", "Pack"));
        Assert.Throws<InvalidDataException>(() => ShaderArchiveStager.MapEntry("repo-main/Shaders/../../escape.fx", "Pack"));
    }

    [Fact]
    public async Task ShaderStagingCopiesFrameworkHeadersToTheRoot()
    {
        using var temp = new TestDirectory();
        var archive = CreateZip(temp.Combine("pack.zip"), ("repo-master/Shaders/ReShade.fxh", "header"), ("repo-master/Shaders/Sub/Effect.fx", "fx"));
        var tree = temp.Directory("tree");

        var count = await ShaderArchiveStager.StageAsync(archive, "CrosireMaster", tree);

        Assert.Equal(2, count);
        Assert.True(File.Exists(Path.Combine(tree, "Shaders", "ReShade.fxh")));
        Assert.True(File.Exists(Path.Combine(tree, "Shaders", "CrosireMaster", "Sub", "Effect.fx")));
    }

    [Fact]
    public void DependenciesResolveBeforeDependents()
    {
        var packs = ShaderPackCatalog.ResolveWithDependencies(["Azen"]);
        Assert.Equal(["SmolbbsoopShaders", "Azen"], packs.Select(pack => pack.Id));
        Assert.Contains("Lilium", ShaderPackCatalog.RecommendedSelection);
        Assert.Throws<InvalidDataException>(() => ShaderPackCatalog.ResolveWithDependencies(["Missing"]));
    }

    [Fact]
    public async Task ReLimiterInstallsRecordsAndRemovesCleanly()
    {
        using var temp = new TestDirectory();
        var (planner, executor, game) = Setup(temp);
        temp.File("game/ReShade.ini", "[GENERAL]\n");

        var plan = await planner.BuildComponentInstallAsync(game, EnhancementKind.ReLimiter);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers));
        var dryRun = await executor.ExecuteAsync(plan, dryRun: true);
        Assert.True(dryRun.Succeeded);
        Assert.False(File.Exists(temp.Combine("game", "relimiter.addon64")));

        var result = await executor.ExecuteAsync(plan, dryRun: false);
        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(temp.Combine("game", "relimiter.addon64")));
        var snapshot = await planner.InspectAsync(game);
        var status = snapshot.Components.Single(item => item.Kind == EnhancementKind.ReLimiter);
        Assert.True(status.Managed);
        Assert.Equal("v1.2.0", status.Version);

        var again = await planner.BuildComponentInstallAsync(game, EnhancementKind.ReLimiter);
        Assert.True(again.IsEmpty);

        var remove = await planner.BuildComponentRemoveAsync(game, EnhancementKind.ReLimiter);
        Assert.True((await executor.ExecuteAsync(remove, false)).Succeeded);
        Assert.False(File.Exists(temp.Combine("game", "relimiter.addon64")));
        Assert.Empty((await EnhancementStateStore.LoadAsync(game.GameRoot)).Files);
    }

    [Fact]
    public async Task ForeignAddonIsNeverOverwrittenOrRemoved()
    {
        using var temp = new TestDirectory();
        var (planner, _, game) = Setup(temp);
        temp.File("game/ReShade.ini", "[GENERAL]\n");
        temp.File("game/relimiter.addon64", "user file");

        var install = await planner.BuildComponentInstallAsync(game, EnhancementKind.ReLimiter);
        var remove = await planner.BuildComponentRemoveAsync(game, EnhancementKind.ReLimiter);

        Assert.False(install.CanExecute);
        Assert.False(remove.CanExecute);
        Assert.Equal("user file", File.ReadAllText(temp.Combine("game", "relimiter.addon64")));
    }

    [Fact]
    public async Task DisplayCommanderReplacesManagedReLimiter()
    {
        using var temp = new TestDirectory();
        var (planner, executor, game) = Setup(temp);
        temp.File("game/ReShade.ini", "[GENERAL]\n");
        Assert.True((await executor.ExecuteAsync(await planner.BuildComponentInstallAsync(game, EnhancementKind.ReLimiter), false)).Succeeded);

        var plan = await planner.BuildComponentInstallAsync(game, EnhancementKind.DisplayCommander);
        Assert.Contains(plan.Steps, step => step.Type == EnhancementStepType.RemoveFile && step.Kind == EnhancementKind.ReLimiter);
        Assert.True((await executor.ExecuteAsync(plan, false)).Succeeded);

        Assert.False(File.Exists(temp.Combine("game", "relimiter.addon64")));
        Assert.True(File.Exists(temp.Combine("game", "zzz_display_commander.addon64")));
        var state = await EnhancementStateStore.LoadAsync(game.GameRoot);
        Assert.Equal([EnhancementKind.DisplayCommander], state.Files.Select(file => file.Kind));
    }

    [Fact]
    public async Task ReFrameworkRequiresReEngineAndAddsDllOverride()
    {
        using var temp = new TestDirectory();
        var (planner, executor, game) = Setup(temp);
        var blocked = await planner.BuildComponentInstallAsync(game, EnhancementKind.ReFramework);
        Assert.False(blocked.CanExecute);

        var reGame = game with { Engine = GameEngine.ReEngine };
        var plan = await planner.BuildComponentInstallAsync(reGame, EnhancementKind.ReFramework);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers));
        Assert.True((await executor.ExecuteAsync(plan, false)).Succeeded);
        Assert.True(File.Exists(temp.Combine("game", "dinput8.dll")));

        var snapshot = await planner.InspectAsync(reGame);
        Assert.Equal(["dinput8"], snapshot.AdditionalDllOverrides);
        Assert.Equal("WINEDLLOVERRIDES=\"dxgi=n,b;dinput8=n,b\" %command%",
            EnhancementPlanner.ComposeLaunchOption("WINEDLLOVERRIDES=\"dxgi=n,b\" %command%", snapshot.AdditionalDllOverrides));
        Assert.Equal("DXVK_HDR=1 WINEDLLOVERRIDES=\"dinput8=n,b\" %command%",
            EnhancementPlanner.ComposeLaunchOption("DXVK_HDR=1 %command%", ["dinput8"]));
    }

    [Fact]
    public async Task OptiPatcherInstallsIntoPluginsOnlyWithOptiScaler()
    {
        using var temp = new TestDirectory();
        var (planner, executor, game) = Setup(temp);
        Assert.False((await planner.BuildComponentInstallAsync(game, EnhancementKind.OptiPatcher)).CanExecute);

        temp.File("game/OptiScaler.ini", "[Plugins]\nLoadAsiPlugins=true\n");
        var plan = await planner.BuildComponentInstallAsync(game, EnhancementKind.OptiPatcher);
        Assert.True((await executor.ExecuteAsync(plan, false)).Succeeded);
        Assert.True(File.Exists(temp.Combine("game", "plugins", "OptiPatcher.asi")));

        var remove = await planner.BuildComponentRemoveAsync(game, EnhancementKind.OptiPatcher);
        Assert.True((await executor.ExecuteAsync(remove, false)).Succeeded);
        Assert.False(File.Exists(temp.Combine("game", "plugins", "OptiPatcher.asi")));
    }

    [Fact]
    public async Task ShaderPacksPreserveUserFolderAndRestoreIniOnRemoval()
    {
        using var temp = new TestDirectory();
        var (planner, executor, game) = Setup(temp);
        temp.File("game/ReShade.ini", "[GENERAL]\nEffectSearchPaths=.\\custom\\**\n");
        temp.File("game/reshade-shaders/Shaders/Mine.fx", "mine");

        var plan = await planner.BuildShaderPlanAsync(game, ["SweetFX"]);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers.Concat(plan.Warnings)));
        Assert.Contains(plan.Steps, step => step.Type == EnhancementStepType.PreserveOriginalShaders);
        var result = await executor.ExecuteAsync(plan, false);
        Assert.True(result.Succeeded, result.Error);

        Assert.True(File.Exists(temp.Combine("game", "reshade-shaders-original", "Shaders", "Mine.fx")));
        Assert.True(File.Exists(temp.Combine("game", "reshade-shaders", "Shaders", "SweetFX", "SweetFX.fx")));
        var ini = File.ReadAllText(temp.Combine("game", "ReShade.ini"));
        Assert.Contains(EnhancementPlanner.EffectSearchPaths, ini);
        Assert.Contains(EnhancementPlanner.TextureSearchPaths, ini);
        var snapshot = await planner.InspectAsync(game);
        Assert.True(snapshot.ShaderTreeManaged);
        Assert.Equal(CommitSha[..12], snapshot.ShaderPacks.Single().Version);

        var remove = await planner.BuildShaderPlanAsync(game, []);
        Assert.True((await executor.ExecuteAsync(remove, false)).Succeeded);
        Assert.True(File.Exists(temp.Combine("game", "reshade-shaders", "Shaders", "Mine.fx")));
        Assert.False(Directory.Exists(temp.Combine("game", "reshade-shaders-original")));
        ini = File.ReadAllText(temp.Combine("game", "ReShade.ini"));
        Assert.Contains("EffectSearchPaths=.\\custom\\**", ini);
        Assert.DoesNotContain("TextureSearchPaths", ini);
    }

    [Fact]
    public async Task ShaderPacksRequireReShade()
    {
        using var temp = new TestDirectory();
        var (planner, _, game) = Setup(temp);
        var plan = await planner.BuildShaderPlanAsync(game, ["SweetFX"]);
        Assert.False(plan.CanExecute);
        Assert.False(Directory.Exists(temp.Combine("game", "reshade-shaders")));
    }

    [Fact]
    public async Task FailedExecutionRollsBackCompletedSteps()
    {
        using var temp = new TestDirectory();
        var (planner, executor, game) = Setup(temp);
        temp.File("game/ReShade.ini", "[GENERAL]\nKeep=1\n");
        var plan = await planner.BuildShaderPlanAsync(game, ["SweetFX"]);
        plan.Steps.Add(new(EnhancementStepType.PlaceFile, EnhancementKind.ReLimiter, temp.Combine("game", "late.addon64"),
            "Place a tampered file", temp.File("tampered.bin", "x"), ExpectedSha256: new string('0', 64)));

        var result = await executor.ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(temp.Combine("game", "reshade-shaders")));
        Assert.Equal("[GENERAL]\nKeep=1\n", File.ReadAllText(temp.Combine("game", "ReShade.ini")));
        Assert.False(EnhancementExecutor.HasPendingRecovery(game.GameRoot));
        Assert.False(File.Exists(EnhancementStateStore.StatePath(game.GameRoot)));
    }

    [Fact]
    public async Task PathsOutsideTheGameAreRejected()
    {
        using var temp = new TestDirectory();
        var (planner, executor, game) = Setup(temp);
        var plan = new EnhancementPlan { Id = "outside", Title = "t", Game = game, ResultingState = new() };
        plan.Steps.Add(new(EnhancementStepType.RemoveFile, EnhancementKind.ReLimiter, temp.File("outside.txt", "keep"), "Remove outside"));

        var result = await executor.ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(temp.Combine("outside.txt")));
        _ = planner;
    }

    private static (EnhancementPlanner Planner, EnhancementExecutor Executor, DeploymentTarget Game) Setup(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var exe = temp.Pe("game/Game.exe");
        var steamGame = new SteamGame(42, "Game", temp.Path, temp.Path, root, temp.Combine("compatdata", "42", "pfx"), exe, root,
            DetectionConfidence.High, "test", GameEngine.Unknown, []);
        var paths = new XdgPaths(temp.Path, new Dictionary<string, string?> { ["XDG_CACHE_HOME"] = temp.Combine("cache") });
        var client = new HttpClient(new EnhancementHandler(temp));
        return (new EnhancementPlanner(new EnhancementSourceClient(client, paths), paths), new EnhancementExecutor(), steamGame.ToDeploymentTarget());
    }

    private static string CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    private sealed class EnhancementHandler(TestDirectory temp) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpResponseMessage response = url switch
            {
                _ when url.Contains("/repos/RankFTW/ReLimiter/releases/latest") => Json(Release("v1.2.0", "RankFTW/ReLimiter", "relimiter.addon64")),
                _ when url.Contains("/repos/pmnoxx/display-commander/releases/tags/latest") => Json(Release("latest", "pmnoxx/display-commander", "zzz_display_commander.addon64")),
                _ when url.Contains("/repos/praydog/REFramework-nightly/releases/latest") => Json(Release("nightly-01", "praydog/REFramework-nightly", "REFramework.zip")),
                _ when url.Contains("/repos/optiscaler/OptiPatcher/releases/tags/rolling") => Json(Release("rolling", "optiscaler/OptiPatcher", "OptiPatcher.asi")),
                _ when url.Contains("/commits/") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CommitSha) },
                _ when url.StartsWith("https://codeload.github.com/CeeJayDK/SweetFX/zip/") =>
                    Bytes(File.ReadAllBytes(CreateZip(temp.Combine($"sweetfx-{Guid.NewGuid():N}.zip"),
                        ("SweetFX-master/Shaders/SweetFX.fx", "technique"), ("SweetFX-master/Textures/lut.png", "png")))),
                _ when url.EndsWith("REFramework.zip") => Bytes(ZipWithPe()),
                _ when url.Contains("/releases/download/") => Bytes(File.ReadAllBytes(temp.Pe($"payload-{Guid.NewGuid():N}.dll", size: 4096))),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private byte[] ZipWithPe()
        {
            var pe = File.ReadAllBytes(temp.Pe($"dinput-{Guid.NewGuid():N}.dll", size: 4096));
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                using var stream = zip.CreateEntry("dinput8.dll").Open();
                stream.Write(pe);
            }
            return buffer.ToArray();
        }

        private static string Release(string tag, string repository, string asset) =>
            $$"""
            {"tag_name":"{{tag}}","html_url":"https://github.com/{{repository}}/releases/tag/{{tag}}","published_at":"2026-01-01T00:00:00Z",
             "assets":[{"name":"{{asset}}","browser_download_url":"https://github.com/{{repository}}/releases/download/{{tag}}/{{asset}}","size":0,"updated_at":"2026-01-02T03:04:05Z"}]}
            """;

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bytes(byte[] bytes) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }
}
