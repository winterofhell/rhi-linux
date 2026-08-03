using System.Text;

namespace RhiLinux.Core;

public enum IniIssueSeverity { Warning, Recoverable, Fatal }

public sealed record IniParseIssue(IniIssueSeverity Severity, int LineNumber, string Message, string Line);

public sealed class IniDocument
{
    private readonly List<string> lines = [];
    private readonly List<IniParseIssue> issues = [];
    private bool hadUtf8Bom;
    private string newline = Environment.NewLine;

    public IReadOnlyList<IniParseIssue> Issues => issues;
    public bool HasFatalIssues => issues.Any(item => item.Severity == IniIssueSeverity.Fatal);
    public bool HasRecoverableIssues => issues.Any(item => item.Severity is IniIssueSeverity.Recoverable or IniIssueSeverity.Warning);
    public bool HadUtf8Bom => hadUtf8Bom;

    public static IniDocument Parse(string text) => Parse(text, strict: false);

    public static IniDocument ParseStrict(string text) => Parse(text, strict: true);

    public static IniDocument Parse(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes.AsSpan(offset));
        var document = Parse(text, strict: false);
        document.hadUtf8Bom = offset > 0;
        return document;
    }

    private static IniDocument Parse(string text, bool strict)
    {
        var document = new IniDocument();
        if (text.Contains("\r\n", StringComparison.Ordinal)) document.newline = "\r\n";
        else if (text.Contains('\n')) document.newline = "\n";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        document.lines.AddRange(normalized.Split('\n'));
        if (document.lines.Count > 0 && document.lines[^1].Length == 0) document.lines.RemoveAt(document.lines.Count - 1);
        for (var index = 0; index < document.lines.Count; index++)
        {
            var line = document.lines[index];
            var trimmed = line.Trim();
            if (trimmed.Contains('\0'))
            {
                var issue = new IniParseIssue(IniIssueSeverity.Fatal, index + 1,
                    "The INI contains a null byte.", line);
                if (strict) throw new InvalidDataException(issue.Message);
                document.issues.Add(issue);
                continue;
            }
            if ((trimmed.StartsWith('[') || trimmed.EndsWith(']')) &&
                (!trimmed.StartsWith('[') || !trimmed.EndsWith(']') || trimmed.Length <= 2))
            {
                var issue = new IniParseIssue(IniIssueSeverity.Recoverable, index + 1,
                    "The INI contains a malformed section header.", line);
                if (strict) throw new InvalidDataException(issue.Message);
                document.issues.Add(issue);
            }
        }
        return document;
    }

    public string? Get(string section, string key)
    {
        var active = string.Empty;
        foreach (var line in lines)
        {
            if (IsSectionHeader(line, out var name))
            {
                active = name;
                continue;
            }
            if (!active.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryReadKey(line, out var currentKey, out var value) &&
                currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    public bool CanSafelyEditManagedKeys(IEnumerable<(string Section, string Key)> managedKeys)
    {
        if (HasFatalIssues) return false;
        foreach (var (section, key) in managedKeys)
        {
            if (string.IsNullOrEmpty(section))
            {
                if (HasRecoverableIssues && Get(string.Empty, key) is not null)
                    return false;
                continue;
            }
            if (FindMalformedSectionClaimingName(section) is not null && !HasValidSection(section))
                return false;
            if (FindMalformedSectionClaimingName(section) is not null && Get(section, key) is not null)
                return false;
        }
        return true;
    }

    public void Set(string section, string key, string value)
    {
        var ranges = new List<(int Start, int End)>();
        if (section.Length == 0)
        {
            var end = lines.FindIndex(line => IsSectionHeader(line, out _));
            ranges.Add((-1, end < 0 ? lines.Count : end));
        }
        else
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (!IsSectionHeader(lines[index], out var name) ||
                    !name.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
                var end = index + 1;
                while (end < lines.Count && !IsSectionHeader(lines[end], out _)) end++;
                ranges.Add((index, end));
            }
            if (ranges.Count == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
                lines.Add($"[{section}]");
                lines.Add($"{key}={value}");
                return;
            }
        }

        var matches = new List<int>();
        foreach (var range in ranges)
        {
            for (var index = range.Start + 1; index < range.End; index++)
                if (IsKey(lines[index], key)) matches.Add(index);
        }
        if (matches.Count == 0)
        {
            lines.Insert(ranges[0].End, $"{key}={value}");
            return;
        }
        lines[matches[0]] = $"{key}={value}";
        for (var index = matches.Count - 1; index > 0; index--) lines.RemoveAt(matches[index]);
    }

    public int ValueCount(string section, string key)
    {
        var active = string.Empty;
        var count = 0;
        foreach (var line in lines)
        {
            if (IsSectionHeader(line, out var name)) active = name;
            else if (active.Equals(section, StringComparison.OrdinalIgnoreCase) && IsKey(line, key)) count++;
        }
        return count;
    }

    public void Remove(string section, string key)
    {
        var active = string.Empty;
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsSectionHeader(lines[i], out var name))
            {
                active = name;
                continue;
            }
            if (!active.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryReadKey(lines[i], out var currentKey, out _) ||
                !currentKey.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveAt(i--);
        }
    }

    public string? FindSectionContaining(string key)
    {
        var active = string.Empty;
        foreach (var line in lines)
        {
            if (IsSectionHeader(line, out var name))
            {
                active = name;
                continue;
            }
            if (TryReadKey(line, out var currentKey, out _) &&
                currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                return active;
        }
        return null;
    }

    public byte[] ToUtf8Bytes()
    {
        var text = ToString();
        var payload = Encoding.UTF8.GetBytes(text);
        if (!hadUtf8Bom) return payload;
        var withBom = new byte[payload.Length + 3];
        withBom[0] = 0xef;
        withBom[1] = 0xbb;
        withBom[2] = 0xbf;
        Buffer.BlockCopy(payload, 0, withBom, 3, payload.Length);
        return withBom;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            builder.Append(lines[index]);
            if (index + 1 < lines.Count || lines[index].Length > 0) builder.Append(newline);
        }
        return builder.ToString();
    }

    private bool HasValidSection(string section)
    {
        foreach (var line in lines)
            if (IsSectionHeader(line, out var name) && name.Equals(section, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private bool SectionIsOnlyMalformed(string section)
    {
        var malformed = FindMalformedSectionClaimingName(section);
        return malformed is not null && !HasValidSection(section);
    }

    private string? FindMalformedSectionClaimingName(string section)
    {
        foreach (var issue in issues)
        {
            var trimmed = issue.Line.Trim();
            if (trimmed.StartsWith('[') &&
                trimmed.Contains(section, StringComparison.OrdinalIgnoreCase) &&
                !IsSectionHeader(issue.Line, out _))
                return issue.Line;
        }
        return null;
    }

    private static bool IsSectionHeader(string line, out string name)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
        {
            name = trimmed[1..^1].Trim();
            return name.Length > 0 && !name.Contains('[') && !name.Contains(']');
        }
        name = string.Empty;
        return false;
    }

    private static bool IsKey(string line, string key) =>
        TryReadKey(line, out var currentKey, out _) &&
        currentKey.Equals(key, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadKey(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith('['))
            return false;
        var separator = trimmed.IndexOf('=');
        if (separator <= 0) return false;
        key = trimmed[..separator].Trim();
        value = trimmed[(separator + 1)..].Trim();
        return key.Length > 0;
    }
}
