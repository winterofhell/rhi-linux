namespace RhiLinux.Steam;

public sealed class VdfObject
{
    private readonly List<KeyValuePair<string, object>> values = [];
    public IReadOnlyList<KeyValuePair<string, object>> Values => values;

    internal void Add(string key, object value) => values.Add(new(key, value));
    public string? GetString(string key) => values.LastOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value as string;
    public VdfObject? GetObject(string key) => values.LastOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value as VdfObject;
    public IEnumerable<KeyValuePair<string, VdfObject>> Objects() => values.Where(x => x.Value is VdfObject).Select(x => new KeyValuePair<string, VdfObject>(x.Key, (VdfObject)x.Value));
}

public static class VdfParser
{
    public static VdfObject Parse(string text)
    {
        var tokenizer = new Tokenizer(text);
        var root = ParseObject(tokenizer, false);
        if (tokenizer.Next().Kind != TokenKind.End) throw tokenizer.Error("Unexpected trailing input.");
        return root;
    }

    private static VdfObject ParseObject(Tokenizer tokenizer, bool expectsClose)
    {
        var result = new VdfObject();
        while (true)
        {
            var key = tokenizer.Next();
            if (key.Kind == TokenKind.End)
            {
                if (expectsClose) throw tokenizer.Error("Unterminated object.");
                return result;
            }
            if (key.Kind == TokenKind.Close)
            {
                if (!expectsClose) throw tokenizer.Error("Unexpected closing brace.");
                return result;
            }
            if (key.Kind is not TokenKind.Value) throw tokenizer.Error("Expected a key.");
            var value = tokenizer.Next();
            if (value.Kind == TokenKind.Open) result.Add(key.Text, ParseObject(tokenizer, true));
            else if (value.Kind == TokenKind.Value) result.Add(key.Text, value.Text);
            else throw tokenizer.Error($"Expected a value for '{key.Text}'.");
        }
    }

    private enum TokenKind { Value, Open, Close, End }
    private readonly record struct Token(TokenKind Kind, string Text);

    private sealed class Tokenizer(string input)
    {
        private int position;
        private int line = 1;

        public Token Next()
        {
            SkipTrivia();
            if (position >= input.Length) return new(TokenKind.End, string.Empty);
            if (input[position] == '{') { position++; return new(TokenKind.Open, "{"); }
            if (input[position] == '}') { position++; return new(TokenKind.Close, "}"); }
            return input[position] == '"' ? ReadQuoted() : ReadBare();
        }

        public FormatException Error(string message) => new($"VDF line {line}: {message}");

        private Token ReadQuoted()
        {
            position++;
            var result = new System.Text.StringBuilder();
            while (position < input.Length)
            {
                var current = input[position++];
                if (current == '"') return new(TokenKind.Value, result.ToString());
                if (current == '\n') line++;
                if (current == '\\' && position < input.Length)
                {
                    var escaped = input[position++];
                    result.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => escaped });
                }
                else result.Append(current);
            }
            throw Error("Unterminated quoted string.");
        }

        private Token ReadBare()
        {
            var start = position;
            while (position < input.Length && !char.IsWhiteSpace(input[position]) && input[position] is not '{' and not '}') position++;
            if (start == position) throw Error("Invalid token.");
            return new(TokenKind.Value, input[start..position]);
        }

        private void SkipTrivia()
        {
            while (position < input.Length)
            {
                if (char.IsWhiteSpace(input[position])) { if (input[position++] == '\n') line++; continue; }
                if (input[position] == '/' && position + 1 < input.Length && input[position + 1] == '/')
                {
                    position += 2;
                    while (position < input.Length && input[position] != '\n') position++;
                    continue;
                }
                break;
            }
        }
    }
}
