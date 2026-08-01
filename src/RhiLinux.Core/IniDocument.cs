using System.Text;

namespace RhiLinux.Core;

public sealed class IniDocument
{
    private readonly List<string> lines = [];

    public static IniDocument Parse(string text)
    {
        var document = new IniDocument();
        document.lines.AddRange(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        if (document.lines.Count > 0 && document.lines[^1].Length == 0) document.lines.RemoveAt(document.lines.Count - 1);
        foreach (var line in document.lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains('\0') ||
                (trimmed.StartsWith('[') || trimmed.EndsWith(']')) &&
                (!trimmed.StartsWith('[') || !trimmed.EndsWith(']') || trimmed.Length <= 2))
                throw new InvalidDataException("The INI contains a malformed section header.");
        }
        return document;
    }

    public string? Get(string section, string key)
    {
        var active = string.Empty;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) active = trimmed[1..^1];
            else if (active.Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                var separator = trimmed.IndexOf('=');
                if (separator > 0 && trimmed[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return trimmed[(separator + 1)..].Trim();
            }
        }
        return null;
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
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                active = trimmed[1..^1];
                continue;
            }
            var separator = trimmed.IndexOf('=');
            if (active.Equals(section, StringComparison.OrdinalIgnoreCase) && separator > 0 &&
                trimmed[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(i--);
        }
    }

    public string? FindSectionContaining(string key)
    {
        var active = string.Empty;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) active = trimmed[1..^1];
            else
            {
                var separator = trimmed.IndexOf('=');
                if (separator > 0 && trimmed[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return active;
            }
        }
        return null;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var line in lines) builder.AppendLine(line);
        return builder.ToString();
    }

    private static bool IsSectionHeader(string line, out string name)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            name = trimmed[1..^1];
            return true;
        }
        name = string.Empty;
        return false;
    }

    private static bool IsKey(string line, string key)
    {
        var separator = line.IndexOf('=');
        return separator > 0 && line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }
}
