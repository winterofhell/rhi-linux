using System.Text;
using System.Text.RegularExpressions;

namespace RhiLinux.Steam;

public enum LaunchTokenKind
{
    EnvironmentAssignment,
    Wrapper,
    Argument,
    CommandPlaceholder
}

public sealed record LaunchToken(LaunchTokenKind Kind, string Text, string? Name = null, string? Value = null);

public sealed record LaunchComposition(
    string Text,
    IReadOnlyList<LaunchToken> Tokens,
    IReadOnlyList<string> Conflicts,
    string Explanation);

public static partial class LaunchCommandComposer
{
    public static IReadOnlyList<LaunchToken> Parse(string? launchOptions)
    {
        if (string.IsNullOrWhiteSpace(launchOptions)) return [];
        var tokens = new List<LaunchToken>();
        foreach (Match match in TokenRegex().Matches(launchOptions.Trim()))
        {
            var text = match.Value;
            if (text.Equals("%command%", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(new(LaunchTokenKind.CommandPlaceholder, text));
                continue;
            }

            var assignment = text.IndexOf('=');
            if (assignment > 0 && !text.StartsWith('-'))
            {
                var name = text[..assignment];
                var value = text[(assignment + 1)..];
                tokens.Add(new(LaunchTokenKind.EnvironmentAssignment, text, name, value));
                continue;
            }

            if (text is "gamemoderun" or "mangohud" or "gamescope")
            {
                tokens.Add(new(LaunchTokenKind.Wrapper, text, text));
                continue;
            }

            tokens.Add(new(LaunchTokenKind.Argument, text));
        }

        return tokens;
    }

    public static LaunchComposition Compose(
        string? existing,
        string? wineDllOverrides = null,
        bool enableGameMode = false,
        bool enableMangoHud = false,
        bool enableHdr = false,
        string? gamescopeArgs = null,
        string? extraArguments = null)
    {
        var tokens = Parse(existing).ToList();
        var conflicts = new List<string>();

        if (!string.IsNullOrWhiteSpace(wineDllOverrides))
        {
            var required = wineDllOverrides.Trim();
            var existingOverride = tokens.FindIndex(token =>
                token.Kind == LaunchTokenKind.EnvironmentAssignment &&
                token.Name is not null &&
                token.Name.Equals("WINEDLLOVERRIDES", StringComparison.OrdinalIgnoreCase));
            if (existingOverride >= 0)
            {
                if (!string.Equals(tokens[existingOverride].Text, required, StringComparison.Ordinal))
                    conflicts.Add("Existing WINEDLLOVERRIDES differs from the managed value.");
                tokens[existingOverride] = new(LaunchTokenKind.EnvironmentAssignment, required, "WINEDLLOVERRIDES",
                    required.Contains('=') ? required[(required.IndexOf('=') + 1)..] : required);
            }
            else
                tokens.Insert(0, new(LaunchTokenKind.EnvironmentAssignment, required, "WINEDLLOVERRIDES",
                    required.Contains('=') ? required[(required.IndexOf('=') + 1)..] : required));
        }

        SetEnvironment(tokens, "PROTON_ENABLE_WAYLAND", enableHdr ? "1" : null, conflicts);
        SetEnvironment(tokens, "DXVK_HDR", enableHdr ? "1" : null, conflicts);

        SetWrapper(tokens, "gamemoderun", enableGameMode);
        SetWrapper(tokens, "mangohud", enableMangoHud);

        if (!string.IsNullOrWhiteSpace(gamescopeArgs))
        {
            tokens.RemoveAll(token =>
                (token.Kind == LaunchTokenKind.Wrapper && token.Name == "gamescope") ||
                (token.Kind == LaunchTokenKind.Argument && token.Text.StartsWith('-')));
            var insertAt = 0;
            tokens.Insert(insertAt++, new(LaunchTokenKind.Wrapper, "gamescope", "gamescope"));
            foreach (var part in Parse(gamescopeArgs))
                if (part.Kind != LaunchTokenKind.CommandPlaceholder)
                    tokens.Insert(insertAt++, part with { Kind = LaunchTokenKind.Argument });
        }

        if (!string.IsNullOrWhiteSpace(extraArguments))
        {
            foreach (var part in Parse(extraArguments))
            {
                if (part.Kind == LaunchTokenKind.CommandPlaceholder) continue;
                if (tokens.Any(token => token.Text.Equals(part.Text, StringComparison.Ordinal))) continue;
                tokens.Add(part);
            }
        }

        if (!tokens.Any(token => token.Kind == LaunchTokenKind.CommandPlaceholder))
            tokens.Add(new(LaunchTokenKind.CommandPlaceholder, "%command%"));

        var text = string.Join(' ', tokens.Select(token => token.Text));
        return new LaunchComposition(
            text,
            tokens,
            conflicts,
            conflicts.Count == 0
                ? "Launch option composition is ready to copy into Steam."
                : "Launch option composition contains conflicts that should be reviewed.");
    }

    public static string Diff(string? current, string proposed)
    {
        var left = current?.Trim() ?? string.Empty;
        var right = proposed.Trim();
        if (string.Equals(left, right, StringComparison.Ordinal)) return "No changes.";
        var builder = new StringBuilder();
        builder.AppendLine("- " + (left.Length == 0 ? "(empty)" : left));
        builder.Append("+ " + right);
        return builder.ToString();
    }

    private static void SetEnvironment(List<LaunchToken> tokens, string name, string? value, List<string> conflicts)
    {
        var index = tokens.FindIndex(token =>
            token.Kind == LaunchTokenKind.EnvironmentAssignment &&
            string.Equals(token.Name, name, StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            if (index >= 0) tokens.RemoveAt(index);
            return;
        }

        var text = $"{name}={value}";
        if (index >= 0)
        {
            if (!string.Equals(tokens[index].Text, text, StringComparison.Ordinal))
                conflicts.Add($"Existing {name} differs from the requested value.");
            tokens[index] = new(LaunchTokenKind.EnvironmentAssignment, text, name, value);
        }
        else
            tokens.Insert(0, new(LaunchTokenKind.EnvironmentAssignment, text, name, value));
    }

    private static void SetWrapper(List<LaunchToken> tokens, string wrapper, bool enabled)
    {
        var index = tokens.FindIndex(token =>
            token.Kind == LaunchTokenKind.Wrapper &&
            string.Equals(token.Name, wrapper, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            if (index >= 0) tokens.RemoveAt(index);
            return;
        }

        if (index >= 0) return;
        var commandIndex = tokens.FindIndex(token => token.Kind == LaunchTokenKind.CommandPlaceholder);
        if (commandIndex < 0) tokens.Add(new(LaunchTokenKind.Wrapper, wrapper, wrapper));
        else tokens.Insert(commandIndex, new(LaunchTokenKind.Wrapper, wrapper, wrapper));
    }

    [GeneratedRegex("""[^\s]+|%command%""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
