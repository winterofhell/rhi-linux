using System.Globalization;
using System.Text;

namespace RhiLinux.Sources;

internal abstract class YamlNode
{
    public sealed class Scalar(string value) : YamlNode
    {
        public string Value { get; } = value;
    }

    public sealed class Mapping : YamlNode
    {
        public Dictionary<string, YamlNode> Children { get; } = new(StringComparer.Ordinal);
    }

    public sealed class Sequence : YamlNode
    {
        public List<YamlNode> Items { get; } = [];
    }
}

internal static class MinimalYaml
{
    public static bool TryParse(string text, out YamlNode.Mapping? root, out string? error)
    {
        root = null;
        error = null;
        try
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var index = 0;
            root = ParseMapping(lines, ref index, 0);
            return true;
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static string? GetString(YamlNode? node, params string[] path)
    {
        var current = node;
        for (var i = 0; i < path.Length; i++)
        {
            if (current is not YamlNode.Mapping mapping ||
                !mapping.Children.TryGetValue(path[i], out current))
                return null;
            if (i == path.Length - 1)
                return current switch
                {
                    YamlNode.Scalar scalar => scalar.Value,
                    _ => null
                };
        }

        return null;
    }

    public static YamlNode.Mapping? GetMapping(YamlNode? node, params string[] path)
    {
        var current = node;
        foreach (var segment in path)
        {
            if (current is not YamlNode.Mapping mapping ||
                !mapping.Children.TryGetValue(segment, out current))
                return null;
        }

        return current as YamlNode.Mapping;
    }

    private static YamlNode.Mapping ParseMapping(string[] lines, ref int index, int indent)
    {
        var mapping = new YamlNode.Mapping();
        while (index < lines.Length)
        {
            var raw = lines[index];
            if (string.IsNullOrWhiteSpace(raw) || IsComment(raw))
            {
                index++;
                continue;
            }

            var lineIndent = CountIndent(raw);
            if (lineIndent < indent) break;
            if (lineIndent > indent)
                throw new FormatException($"Unexpected indentation at line {index + 1}.");

            var content = raw.Trim();
            if (content.StartsWith('-'))
                throw new FormatException($"Expected mapping entry at line {index + 1}.");

            var colon = FindUnquotedColon(content);
            if (colon < 0)
                throw new FormatException($"Missing ':' at line {index + 1}.");

            var key = Unquote(content[..colon].Trim());
            var remainder = content[(colon + 1)..].Trim();
            index++;

            if (remainder.Length == 0 || remainder.StartsWith('#'))
            {
                if (index < lines.Length)
                {
                    SkipBlank(lines, ref index);
                    if (index < lines.Length)
                    {
                        var nextIndent = CountIndent(lines[index]);
                        if (nextIndent > indent)
                        {
                            var next = lines[index].TrimStart();
                            mapping.Children[key] = next.StartsWith('-')
                                ? ParseSequence(lines, ref index, nextIndent)
                                : ParseMapping(lines, ref index, nextIndent);
                            continue;
                        }
                    }
                }

                mapping.Children[key] = new YamlNode.Scalar(string.Empty);
            }
            else
            {
                mapping.Children[key] = new YamlNode.Scalar(Unquote(StripInlineComment(remainder)));
            }
        }

        return mapping;
    }

    private static YamlNode.Sequence ParseSequence(string[] lines, ref int index, int indent)
    {
        var sequence = new YamlNode.Sequence();
        while (index < lines.Length)
        {
            var raw = lines[index];
            if (string.IsNullOrWhiteSpace(raw) || IsComment(raw))
            {
                index++;
                continue;
            }

            var lineIndent = CountIndent(raw);
            if (lineIndent < indent) break;
            if (lineIndent > indent)
                throw new FormatException($"Unexpected sequence indentation at line {index + 1}.");

            var content = raw.Trim();
            if (!content.StartsWith("- ", StringComparison.Ordinal) && content != "-")
                break;

            var remainder = content == "-" ? string.Empty : content[2..].Trim();
            index++;
            if (remainder.Length == 0 || remainder.StartsWith('#'))
            {
                SkipBlank(lines, ref index);
                if (index < lines.Length && CountIndent(lines[index]) > indent)
                    sequence.Items.Add(ParseMapping(lines, ref index, CountIndent(lines[index])));
                else
                    sequence.Items.Add(new YamlNode.Scalar(string.Empty));
            }
            else if (FindUnquotedColon(remainder) >= 0)
            {
                var synthetic = new string(' ', indent + 2) + remainder;
                var nestedLines = new List<string> { synthetic };
                while (index < lines.Length)
                {
                    var nestedRaw = lines[index];
                    if (string.IsNullOrWhiteSpace(nestedRaw) || IsComment(nestedRaw))
                    {
                        index++;
                        continue;
                    }

                    var nestedIndent = CountIndent(nestedRaw);
                    if (nestedIndent <= indent) break;
                    nestedLines.Add(nestedRaw);
                    index++;
                }

                var nestedIndex = 0;
                var nestedArray = nestedLines.ToArray();
                sequence.Items.Add(ParseMapping(nestedArray, ref nestedIndex, indent + 2));
            }
            else
            {
                sequence.Items.Add(new YamlNode.Scalar(Unquote(StripInlineComment(remainder))));
            }
        }

        return sequence;
    }

    private static void SkipBlank(string[] lines, ref int index)
    {
        while (index < lines.Length && (string.IsNullOrWhiteSpace(lines[index]) || IsComment(lines[index])))
            index++;
    }

    private static bool IsComment(string raw)
    {
        var trimmed = raw.TrimStart();
        return trimmed.StartsWith('#');
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') count++;
            else if (ch == '\t') count += 2;
            else break;
        }

        return count;
    }

    private static int FindUnquotedColon(string content)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch == '\'' && !inDouble) inSingle = !inSingle;
            else if (ch == '"' && !inSingle) inDouble = !inDouble;
            else if (ch == ':' && !inSingle && !inDouble) return i;
        }

        return -1;
    }

    private static string StripInlineComment(string value)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\'' && !inDouble) inSingle = !inSingle;
            else if (ch == '"' && !inSingle) inDouble = !inDouble;
            else if (ch == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(value[i - 1])))
                return value[..i].TrimEnd();
        }

        return value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            {
                var inner = value[1..^1];
                if (value[0] == '"')
                    return inner.Replace("\\\"", "\"", StringComparison.Ordinal)
                        .Replace("\\n", "\n", StringComparison.Ordinal);
                return inner.Replace("''", "'", StringComparison.Ordinal);
            }
        }

        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "~", StringComparison.Ordinal))
            return string.Empty;
        if (bool.TryParse(value, out _))
            return value.ToLowerInvariant();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return value;
        return value;
    }
}
