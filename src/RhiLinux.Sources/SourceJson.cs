using System.Text.Json;

namespace RhiLinux.Sources;

internal static class SourceJson
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static bool TryParseDocument(string text, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            document = JsonDocument.Parse(text, DocumentOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task<(bool Ok, JsonDocument? Document, string? Error)> TryLoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (!TryParseDocument(text, out var document) || document is null)
                return (false, null, "JSON could not be parsed.");
            return (true, document, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, null, exception.Message);
        }
    }

    public static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var property) &&
                property.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                return property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : property.ToString();
            }
        }

        return null;
    }

    public static bool? GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.True) return true;
            if (property.ValueKind == JsonValueKind.False) return false;
            if (property.ValueKind == JsonValueKind.String &&
                bool.TryParse(property.GetString(), out var parsed))
                return parsed;
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
                return number != 0;
        }

        return null;
    }

    public static IEnumerable<JsonElement> EnumerateObjectOrArray(JsonElement root, string? arrayProperty = null)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        if (arrayProperty is not null &&
            root.TryGetProperty(arrayProperty, out var nested) &&
            nested.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nested.EnumerateArray())
                yield return item;
            yield break;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
                yield return property.Value;
        }
    }

    public static Dictionary<string, string> CollectAttributes(JsonElement element, params string[] keys)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return attributes;
        foreach (var key in keys)
        {
            var value = GetString(element, key);
            if (!string.IsNullOrWhiteSpace(value))
                attributes[key] = value!;
        }

        return attributes;
    }
}
