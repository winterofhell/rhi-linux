using System.Globalization;
using System.Text;
using RhiLinux.Core;

namespace RhiLinux.Gui;

public static class GameSearch
{
    public static IReadOnlyList<SteamGame> Rank(IEnumerable<SteamGame> games, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return games.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var normalizedQuery = Normalize(query);
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

    public static int Score(SteamGame game, string normalizedQuery, IReadOnlyList<string>? tokens = null)
    {
        tokens ??= Tokenize(normalizedQuery);
        var appId = game.AppId.ToString(CultureInfo.InvariantCulture);
        if (appId == normalizedQuery) return 10_000;
        if (appId.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 9_000;

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
