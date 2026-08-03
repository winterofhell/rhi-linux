using System.Text.RegularExpressions;
using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed record LaunchOptionObservation(
    LaunchOptionStatus Status,
    string RequiredOption,
    string? DetectedOption,
    string Explanation);

public sealed record LaunchOptionFragment(
    string Text,
    LaunchOptionFragmentOwnership Ownership);

public sealed record ComposedLaunchOption(
    string Text,
    IReadOnlyList<LaunchOptionFragment> Fragments,
    LaunchOptionStatus Status,
    string Explanation);

public static partial class SteamLaunchOptionService
{
    public const string ProtonEnableWayland = "PROTON_ENABLE_WAYLAND=1";
    public const string DxvkHdr = "DXVK_HDR=1";

    private static readonly Regex WineOverrideFragment = WineOverrideRegex();
    private static readonly Regex ProtonWaylandFragment = ProtonWaylandRegex();
    private static readonly Regex DxvkHdrFragment = DxvkHdrRegex();

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

        if (ContainsRequiredFragments(detected, required))
        {
            return new(LaunchOptionStatus.Correct, required, detected,
                "The required managed launch-option fragments match the current stack.");
        }

        if (WineOverrideFragment.IsMatch(detected) ||
            ProtonWaylandFragment.IsMatch(detected) ||
            DxvkHdrFragment.IsMatch(detected))
        {
            return new(LaunchOptionStatus.NeedsUpdate, required, detected,
                "Steam already has related launch-option values, but they do not match the current managed stack.");
        }

        return new(LaunchOptionStatus.Missing, required, detected,
            "Unrelated Steam launch arguments were found. Add the required managed fragments without removing them.");
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

    public static string ComposeSuggestedLaunchOption(
        string? proxyName,
        bool includeHdr,
        string? existingLaunchOptions = null)
    {
        var composed = Compose(proxyName, includeHdr, existingLaunchOptions, manageHdr: includeHdr,
            preserveUnrelatedArguments: false);
        return composed.Text;
    }

    public static ComposedLaunchOption Compose(
        string? proxyName,
        bool includeHdr,
        string? existingLaunchOptions = null,
        bool manageHdr = false,
        bool preserveUnrelatedArguments = false)
    {
        var existing = existingLaunchOptions ?? string.Empty;
        var fragments = new List<LaunchOptionFragment>();
        var preserved = existing;
        var hadWayland = ProtonWaylandFragment.IsMatch(existing);
        var hadHdr = DxvkHdrFragment.IsMatch(existing);
        var hadOverride = WineOverrideFragment.IsMatch(existing);

        if (includeHdr && manageHdr)
        {
            fragments.Add(new(ProtonEnableWayland, LaunchOptionFragmentOwnership.RhiLinuxManaged));
            fragments.Add(new(DxvkHdr, LaunchOptionFragmentOwnership.RhiLinuxManaged));
            if (hadWayland) preserved = ProtonWaylandFragment.Replace(preserved, " ").Trim();
            if (hadHdr) preserved = DxvkHdrFragment.Replace(preserved, " ").Trim();
        }
        else
        {
            if (hadWayland)
            {
                preserved = ProtonWaylandFragment.Replace(preserved, " ").Trim();
                if (preserveUnrelatedArguments)
                    fragments.Add(new(ProtonEnableWayland, LaunchOptionFragmentOwnership.PreExisting));
            }

            if (hadHdr)
            {
                preserved = DxvkHdrFragment.Replace(preserved, " ").Trim();
                if (preserveUnrelatedArguments)
                    fragments.Add(new(DxvkHdr, LaunchOptionFragmentOwnership.PreExisting));
            }
        }

        string? requiredOverride = null;
        if (!string.IsNullOrWhiteSpace(proxyName))
        {
            var leaf = Path.GetFileNameWithoutExtension(proxyName);
            requiredOverride = $"WINEDLLOVERRIDES=\"{leaf}=n,b\"";
            if (hadOverride)
                preserved = WineOverrideFragment.Replace(preserved, " ").Trim();
            fragments.Add(new(requiredOverride, LaunchOptionFragmentOwnership.RhiLinuxManaged));
        }

        preserved = CollapseWhitespace(preserved
            .Replace("%command%", " ", StringComparison.OrdinalIgnoreCase));
        var unrelated = preserveUnrelatedArguments
            ? SplitTokens(preserved)
                .Where(token => !token.Equals("%command%", StringComparison.OrdinalIgnoreCase))
                .Select(token => new LaunchOptionFragment(token, LaunchOptionFragmentOwnership.UserManaged))
                .ToArray()
            : [];

        var ordered = unrelated
            .Concat(fragments.Where(x => x.Text.StartsWith("PROTON_", StringComparison.OrdinalIgnoreCase) ||
                                         x.Text.StartsWith("DXVK_", StringComparison.OrdinalIgnoreCase)))
            .Concat(fragments.Where(x => x.Text.StartsWith("WINEDLLOVERRIDES", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(x => NormalizeFragment(x.Text), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var text = string.Join(' ', ordered.Select(x => x.Text).Append("%command%"));
        var status = existing.Length == 0
            ? LaunchOptionStatus.Missing
            : ContainsRequiredFragments(existing, text)
                ? LaunchOptionStatus.Correct
                : WineOverrideFragment.IsMatch(existing) || ProtonWaylandFragment.IsMatch(existing) || DxvkHdrFragment.IsMatch(existing)
                    ? LaunchOptionStatus.NeedsUpdate
                    : LaunchOptionStatus.Missing;
        var explanation = includeHdr
            ? "Optional HDR guidance for RenoDX. Unrelated Steam launch arguments are not recommended."
            : "Managed WINEDLLOVERRIDES fragments are composed from the active proxy.";
        return new(text, ordered.Append(new("%command%", LaunchOptionFragmentOwnership.RhiLinuxManaged)).ToArray(),
            status, explanation);
    }

    public static string RemoveManagedHdrFragments(string existingLaunchOptions, bool removeOnlyManaged)
    {
        if (!removeOnlyManaged) return existingLaunchOptions;
        var value = existingLaunchOptions ?? string.Empty;
        value = ProtonWaylandFragment.Replace(value, " ");
        value = DxvkHdrFragment.Replace(value, " ");
        value = CollapseWhitespace(value);
        if (value.Length == 0) return "%command%";
        if (!value.Contains("%command%", StringComparison.OrdinalIgnoreCase))
            value = $"{value} %command%";
        return CollapseWhitespace(value);
    }

    public static string GenerateHdrGuidance(string? proxyName) =>
        ComposeSuggestedLaunchOption(proxyName, includeHdr: true);

    private static bool ContainsRequiredFragments(string detected, string required)
    {
        foreach (var token in SplitTokens(required))
        {
            if (token.Equals("%command%", StringComparison.OrdinalIgnoreCase)) continue;
            if (token.StartsWith("WINEDLLOVERRIDES", StringComparison.OrdinalIgnoreCase))
            {
                if (!ContainsRequiredOverride(detected, token)) return false;
                continue;
            }
            if (!detected.Contains(token, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
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

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', SplitTokens(value));

    private static IEnumerable<string> SplitTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        var matches = TokenRegex().Matches(value);
        foreach (Match match in matches)
        {
            var token = match.Value.Trim();
            if (token.Length > 0) yield return token;
        }
    }

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

    [GeneratedRegex("""PROTON_ENABLE_WAYLAND\s*=\s*\S+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtonWaylandRegex();

    [GeneratedRegex("""DXVK_HDR\s*=\s*\S+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DxvkHdrRegex();

    [GeneratedRegex("""("[^"]+"|\S+)""", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
