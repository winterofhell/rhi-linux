using System.Globalization;
using System.Text;
using RhiLinux.Core;

namespace RhiLinux.Gui;

public static class GameSearch
{
    private static readonly HashSet<string> RecognizedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "store", "launcher", "status", "engine", "has"
    };

    public static IReadOnlyList<InstalledGame> Rank(
        IEnumerable<InstalledGame> games,
        string? query,
        IReadOnlyDictionary<string, GameReadinessState>? readiness = null,
        IReadOnlySet<GameInstallId>? updateable = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return games.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var parsed = Parse(query);
        var normalizedFilters = parsed.Filters
            .Select(filter => (filter.Key, Value: Normalize(filter.Value)))
            .ToArray();
        games = games.Where(game => MatchesFilters(game, normalizedFilters, readiness, updateable));
        var normalizedQuery = Normalize(string.Join(' ', parsed.Text));
        if (normalizedQuery.Length == 0)
            return games.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var tokens = Tokenize(normalizedQuery);
        return games
            .Select(game => (Game: game, Score: Score(game, normalizedQuery, tokens)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Game.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Game)
            .ToArray();
    }

    public static (IReadOnlyList<(string Key, string Value)> Filters, IReadOnlyList<string> Text) Parse(string query)
    {
        var filters = new List<(string Key, string Value)>();
        var text = new List<string>();
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf(':');
            if (separator > 0 && separator < token.Length - 1 &&
                RecognizedKeys.Contains(token[..separator]))
                filters.Add((token[..separator].ToLowerInvariant(), token[(separator + 1)..].ToLowerInvariant()));
            else
                text.Add(token);
        }
        return (filters, text);
    }

    private static bool MatchesFilters(
        InstalledGame game,
        IReadOnlyList<(string Key, string Value)> filters,
        IReadOnlyDictionary<string, GameReadinessState>? readiness,
        IReadOnlySet<GameInstallId>? updateable)
    {
        foreach (var (key, value) in filters)
        {
            var matches = key switch
            {
                "store" => Normalize(game.StoreBadge) == value ||
                           Normalize(game.Store.ToString()) == value,
                "launcher" => Normalize(game.LauncherBadge) == value,
                "engine" => Normalize(game.Engine.ToString()) == value ||
                            game.Engine == GameEngine.UnrealLegacy && value == "unreal",
                "has" when value == "update" => updateable?.Contains(game.InstallId) == true,
                "status" => MatchesStatus(game, value, readiness),
                _ => true
            };
            if (!matches) return false;
        }
        return true;
    }

    private static bool MatchesStatus(
        InstalledGame game,
        string value,
        IReadOnlyDictionary<string, GameReadinessState>? readiness)
    {
        if (readiness is null || !readiness.TryGetValue(game.EffectiveInstallId, out var state)) return false;
        return value switch
        {
            "ready" => state == GameReadinessState.Ready,
            "warning" or "warnings" => state == GameReadinessState.ReadyWithWarnings,
            "blocked" => state is GameReadinessState.NeedsConfiguration or GameReadinessState.NeedsUserSelection,
            "unsupported" => state is GameReadinessState.Unsupported or GameReadinessState.Unavailable,
            "failed" => state == GameReadinessState.Error,
            _ => false
        };
    }

    public static int Score(InstalledGame game, string normalizedQuery, IReadOnlyList<string>? tokens = null)
    {
        tokens ??= Tokenize(normalizedQuery);
        if (game.SteamAppId is { } steamAppId)
        {
            var appId = steamAppId.ToString(CultureInfo.InvariantCulture);
            if (appId == normalizedQuery) return 10_000;
            if (appId.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 9_000;
        }

        if (game.ExternalId is { Length: > 0 } external)
        {
            var normalizedExternal = Normalize(external);
            if (normalizedExternal == normalizedQuery) return 9_500;
            if (normalizedExternal.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 8_500;
        }

        var title = Normalize(game.Name);
        if (title == normalizedQuery) return 8_000;
        if (title.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 7_000;

        var titleTokens = Tokenize(title);
        if (tokens.Count > 0 && tokens.All(token => titleTokens.Any(titleToken =>
                titleToken.Equals(token, StringComparison.Ordinal) ||
                titleToken.StartsWith(token, StringComparison.Ordinal))))
            return 6_000 - Math.Min(titleTokens.Count, 100);

        var executable = Normalize(Path.GetFileNameWithoutExtension(game.Executable ?? string.Empty));
        if (executable.Length > 0 &&
            (executable == normalizedQuery || executable.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
             tokens.Any(token => executable.Contains(token, StringComparison.Ordinal))))
            return 5_000;

        var engine = Normalize(game.Engine.ToString());
        if (engine.Length > 0 &&
            (engine == normalizedQuery || engine.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
             tokens.Any(token => engine.Contains(token, StringComparison.Ordinal))))
            return 4_000;

        if (title.Contains(normalizedQuery, StringComparison.Ordinal) ||
            tokens.Any(token => title.Contains(token, StringComparison.Ordinal)))
            return 1_000;

        return 0;
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(Rune.ToLowerInvariant(rune));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }

    public static IReadOnlyList<string> Tokenize(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
