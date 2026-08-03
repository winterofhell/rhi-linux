using System.Text;

namespace RhiLinux.Core;

public static class BinaryMarkerScanner
{
    private const int ChunkSize = 1024 * 1024;

    public static bool ContainsAny(string path, params string[] markers)
    {
        if (markers.Length == 0 || !File.Exists(path)) return false;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ContainsAny(stream, markers);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool ContainsAny(Stream stream, params string[] markers)
    {
        if (markers.Length == 0) return false;
        var asciiNeedles = markers
            .Where(marker => !string.IsNullOrEmpty(marker))
            .Select(marker => Encoding.ASCII.GetBytes(marker))
            .Where(bytes => bytes.Length > 0)
            .ToArray();
        var unicodeNeedles = markers
            .Where(marker => !string.IsNullOrEmpty(marker))
            .Select(marker => Encoding.Unicode.GetBytes(marker))
            .Where(bytes => bytes.Length > 0)
            .ToArray();
        if (asciiNeedles.Length == 0 && unicodeNeedles.Length == 0) return false;

        var overlap = Math.Max(
            asciiNeedles.Length == 0 ? 0 : asciiNeedles.Max(x => x.Length),
            unicodeNeedles.Length == 0 ? 0 : unicodeNeedles.Max(x => x.Length));
        if (overlap > 0) overlap -= 1;

        var buffer = new byte[ChunkSize];
        var carry = Array.Empty<byte>();
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            var window = carry.Length == 0
                ? buffer.AsSpan(0, read)
                : Concat(carry, buffer.AsSpan(0, read));
            if (ContainsInsensitive(window, asciiNeedles) || ContainsInsensitive(window, unicodeNeedles))
                return true;
            if (overlap == 0 || window.Length == 0)
            {
                carry = [];
                continue;
            }

            var keep = Math.Min(overlap, window.Length);
            carry = window.Slice(window.Length - keep).ToArray();
        }

        return false;
    }

    private static ReadOnlySpan<byte> Concat(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var combined = new byte[left.Length + right.Length];
        left.CopyTo(combined);
        right.CopyTo(combined.AsSpan(left.Length));
        return combined;
    }

    private static bool ContainsInsensitive(ReadOnlySpan<byte> haystack, IReadOnlyList<byte[]> needles)
    {
        foreach (var needle in needles)
        {
            if (IndexOfInsensitive(haystack, needle) >= 0) return true;
        }

        return false;
    }

    private static int IndexOfInsensitive(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (AsciiFold(haystack[i + j]) != AsciiFold(needle[j]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched) return i;
        }

        return -1;
    }

    private static byte AsciiFold(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
}
