using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Mods;

internal static partial class CliApplication
{
    private static async Task<int> HandleEnhance(InstalledGame game, Options options, XdgPaths xdg)
    {
        if (game.IsNativeLinux || !game.IsActionable)
            throw new InvalidOperationException($"{game.Name} is not a Proton game that can receive enhancements.");
        var target = game.ToDeploymentTarget();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("rhi-linux");
        var planner = new EnhancementPlanner(new EnhancementSourceClient(httpClient, xdg), xdg);
        var executor = new EnhancementExecutor();

        if (options.Has("recover"))
        {
            var recovered = await executor.RecoverAsync(target.GameRoot);
            Console.WriteLine(recovered ? "Recovery completed." : "Recovery could not restore every file.");
            return recovered ? 0 : 1;
        }

        var actions = new[] { "install", "remove", "shaders", "remove-all" }.Count(options.Has);
        if (actions > 1) throw new ArgumentException("Choose only one of --install, --remove, --shaders, or --remove-all.");
        if (actions == 0)
        {
            var snapshot = await planner.InspectAsync(target);
            Write(snapshot, options.Has("json"), value => PrintEnhancements(game, value));
            return snapshot.RecoveryPending ? 3 : 0;
        }

        var force = options.Has("refresh");
        EnhancementPlan plan;
        if (options.Value("install") is { } install)
            plan = await planner.BuildComponentInstallAsync(target, ParseEnhancement(install), CancellationToken.None, force);
        else if (options.Value("remove") is { } remove)
            plan = ParseEnhancementOrShaders(remove) is { } kind
                ? await planner.BuildComponentRemoveAsync(target, kind)
                : await planner.BuildShaderPlanAsync(target, [], null, CancellationToken.None, force);
        else if (options.Value("shaders") is { } shaders)
        {
            var ids = shaders.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? Array.Empty<string>()
                : shaders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var id in ids)
                if (ShaderPackCatalog.Find(id) is null) throw new ArgumentException($"Unknown shader pack '{id}'. Run 'shader-packs' for the catalog.");
            var progress = options.Has("json") ? null : new Progress<string>(message => Console.Error.WriteLine(message));
            plan = await planner.BuildShaderPlanAsync(target, ids, progress, CancellationToken.None, force);
        }
        else
            plan = await planner.BuildRemoveEverythingAsync(target);

        try
        {
            var apply = options.Has("apply") && options.Has("yes");
            if (apply && game.RequiresConfirmation && !options.Has("allow-anti-cheat"))
                throw new InvalidOperationException("Anti-cheat confirmation requires --allow-anti-cheat.");
            PrintEnhancementPlan(plan, options.Has("json"));
            if (!plan.CanExecute) return plan.Blockers.Count > 0 ? 3 : 0;
            if (!apply)
            {
                if (!options.Has("json")) Console.WriteLine("Preview only. Add --apply --yes to make these changes.");
                return 0;
            }
            var result = await executor.ExecuteAsync(plan, false);
            if (options.Has("json")) Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            else Console.WriteLine(result.Succeeded ? "Changes applied." : $"Failed: {result.Error}");
            return result.Succeeded ? 0 : 1;
        }
        finally
        {
            EnhancementPlanner.Discard(plan);
        }
    }

    private static int PrintShaderPackCatalog(Options options)
    {
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(ShaderPackCatalog.All.Select(pack => new
            {
                pack.Id,
                pack.Name,
                pack.Description,
                Category = pack.Category.ToString(),
                pack.Repository,
                pack.Requires
            }), JsonOptions));
            return 0;
        }
        foreach (var group in ShaderPackCatalog.All.GroupBy(pack => pack.Category))
        {
            Console.WriteLine(group.Key.ToString().ToUpperInvariant());
            foreach (var pack in group)
                Console.WriteLine($"  {pack.Id,-24} {pack.Name} · {pack.Repository}");
        }
        return 0;
    }

    private static void PrintEnhancements(InstalledGame game, EnhancementSnapshot snapshot)
    {
        Console.WriteLine($"{game.Name} ({game.ExternalId ?? game.EffectiveInstallId})");
        if (snapshot.RecoveryPending) Console.WriteLine("  An interrupted enhancement change needs recovery: run with --recover.");
        foreach (var component in snapshot.Components)
        {
            var state = !component.Applicable ? $"n/a ({component.NotApplicableReason})"
                : component.Managed ? $"installed {component.Version}"
                : component.Installed ? "installed manually"
                : "not installed";
            Console.WriteLine($"  {component.Name,-20} {state}");
        }
        Console.WriteLine(snapshot.ShaderTreeManaged
            ? $"  {"Shader packs",-20} {string.Join(", ", snapshot.ShaderPacks.Select(pack => pack.Id))}"
            : $"  {"Shader packs",-20} {(snapshot.ForeignShaderTree ? "existing unmanaged folder" : "none")}");
        if (snapshot.AdditionalDllOverrides.Count > 0)
            Console.WriteLine($"  Launch option        {EnhancementPlanner.ComposeLaunchOption(null, snapshot.AdditionalDllOverrides)}");
    }

    private static void PrintEnhancementPlan(EnhancementPlan plan, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                plan.Title,
                plan.CanExecute,
                plan.Blockers,
                plan.Warnings,
                Steps = plan.Steps.Select(step => new { Type = step.Type.ToString(), step.Description, step.Target }),
                Downloads = plan.Downloads.Select(download => new { download.Name, download.Version, Source = download.Source.ToString(), download.Size, download.Cached })
            }, JsonOptions));
            return;
        }
        Console.WriteLine(plan.Title);
        foreach (var blocker in plan.Blockers) Console.WriteLine($"  blocked: {blocker}");
        foreach (var warning in plan.Warnings) Console.WriteLine($"  note: {warning}");
        for (var index = 0; index < plan.Steps.Count; index++)
            Console.WriteLine($"  {index + 1,3}. {plan.Steps[index].Description}");
        foreach (var download in plan.Downloads)
            Console.WriteLine($"  download: {download.Name} {download.Version}{(download.Cached ? " (cached)" : string.Empty)}");
        if (plan.Steps.Count == 0 && plan.Blockers.Count == 0) Console.WriteLine("  Nothing to change.");
    }

    private static EnhancementKind ParseEnhancement(string value) =>
        ParseEnhancementOrShaders(value) ?? throw new ArgumentException("Use --shaders to manage shader packs.");

    private static EnhancementKind? ParseEnhancementOrShaders(string value) => value.ToLowerInvariant().Replace("_", "-") switch
    {
        "relimiter" => EnhancementKind.ReLimiter,
        "display-commander" or "displaycommander" => EnhancementKind.DisplayCommander,
        "reframework" => EnhancementKind.ReFramework,
        "optipatcher" => EnhancementKind.OptiPatcher,
        "shaders" or "shader-packs" => null,
        _ => throw new ArgumentException($"Unknown enhancement '{value}'. Use relimiter, display-commander, reframework, optipatcher, or shaders.")
    };
}
