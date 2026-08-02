using System.Text.RegularExpressions;
using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed record LaunchOptionObservation(
    LaunchOptionStatus Status,
    string RequiredOption,
    string? DetectedOption,
    string Explanation);

public static partial class SteamLaunchOptionService
{
    private static readonly Regex WineOverrideFragment = WineOverrideRegex();

    public static LaunchOptionObservation Observe(
        SteamGame game,
        string? requiredOption,
        string? detectedLaunchOptions = null)
    {
        var required = requiredOption?.Trim() ?? string.Empty;
        if (required.Length == 0)
        {
            return new(LaunchOptionStatus.NotRequired, string.Empty, detectedLaunchOptions,
                "No WINEDLLOVERRIDES launch option is required for the current layout.");
        }

        if (detectedLaunchOptions is null)
        {
            return new(LaunchOptionStatus.NotDetected, required, null,
                "Steam launch options were not detected. Copy the required value manually.");
        }

        var detected = detectedLaunchOptions.Trim();
        if (detected.Length == 0)
        {
            return new(LaunchOptionStatus.Missing, required, detected,
                "The required WINEDLLOVERRIDES fragment is missing from Steam launch options.");
        }

        if (ContainsRequiredOverride(detected, required))
        {
            return new(LaunchOptionStatus.Correct, required, detected,
                "The required WINEDLLOVERRIDES fragment matches the active proxy.");
        }

        if (WineOverrideFragment.IsMatch(detected))
        {
            return new(LaunchOptionStatus.NeedsUpdate, required, detected,
                "Steam already has a WINEDLLOVERRIDES value, but it does not match the active proxy.");
        }

        return new(LaunchOptionStatus.Missing, required, detected,
            "Unrelated Steam launch arguments were found. Add the required WINEDLLOVERRIDES fragment without removing them.");
    }

    public static async Task<string?> TryReadLaunchOptionsAsync(
        SteamGame game,
        CancellationToken cancellationToken = default)
    {
        foreach (var path in CandidateLocalConfigPaths(game.SteamRoot))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var root = VdfParser.Parse(text);
                var value = FindLaunchOptions(root, game.AppId);
                if (value is not null) return value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                // Fall through to other candidate paths.
            }
        }

        return null;
    }

    public static string ExtractManagedOverrideFragment(string launchOptions)
    {
        var match = WineOverrideFragment.Match(launchOptions);
        return match.Success ? match.Value : string.Empty;
    }

    public static string MergeManagedOverride(string existingLaunchOptions, string requiredOption)
    {
        var requiredFragment = ExtractManagedOverrideFragment(requiredOption);
        if (requiredFragment.Length == 0) return existingLaunchOptions;

        var existing = existingLaunchOptions ?? string.Empty;
        if (WineOverrideFragment.IsMatch(existing))
            return WineOverrideFragment.Replace(existing, requiredFragment).Trim();

        if (existing.Trim().Length == 0) return requiredOption.Trim();
        if (existing.Contains("%command%", StringComparison.OrdinalIgnoreCase))
            return existing.Replace("%command%", $"{requiredFragment} %command%", StringComparison.OrdinalIgnoreCase);

        return $"{existing.TrimEnd()} {requiredFragment} %command%".Trim();
    }

    private static bool ContainsRequiredOverride(string detected, string required)
    {
        var requiredFragment = ExtractManagedOverrideFragment(required);
        var detectedFragment = ExtractManagedOverrideFragment(detected);
        if (requiredFragment.Length == 0) return false;
        if (detectedFragment.Length > 0)
            return string.Equals(NormalizeFragment(detectedFragment), NormalizeFragment(requiredFragment), StringComparison.OrdinalIgnoreCase);
        return detected.Contains(requiredFragment, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFragment(string fragment) =>
        fragment.Replace(" ", string.Empty, StringComparison.Ordinal);

    private static IEnumerable<string> CandidateLocalConfigPaths(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) yield break;
        foreach (var userDirectory in Directory.EnumerateDirectories(userdata))
            yield return Path.Combine(userDirectory, "config", "localconfig.vdf");
    }

    private static string? FindLaunchOptions(VdfObject root, uint appId)
    {
        var appIdText = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (_, value) in Walk(root))
        {
            if (value is not VdfObject node) continue;
            if (!node.Objects().Any(pair => pair.Key.Equals(appIdText, StringComparison.Ordinal))) continue;
            var app = node.GetObject(appIdText);
            var launch = app?.GetString("LaunchOptions");
            if (launch is not null) return launch;
        }

        return null;
    }

    private static IEnumerable<(string Key, object Value)> Walk(VdfObject root)
    {
        foreach (var pair in root.Values)
        {
            yield return (pair.Key, pair.Value);
            if (pair.Value is VdfObject child)
                foreach (var nested in Walk(child))
                    yield return nested;
        }
    }

    [GeneratedRegex("""WINEDLLOVERRIDES\s*=\s*(?:"[^"]*"|[^\s]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WineOverrideRegex();
}
