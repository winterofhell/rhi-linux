using System.Buffers;
using System.Text;

namespace RhiLinux.Core;

public static class BinaryMarkerScanner
{
    private const int ChunkSize = 1024 * 1024;

    public static bool ContainsAny(string path, params string[] markers)
    {
        if (markers.Length == 0 || !File.Exists(path)) return false;
        return ContainsEach(path, [markers])[0];
    }

    public static bool ContainsAny(Stream stream, params string[] markers) =>
        ContainsEach(stream, [markers])[0];

    public static bool ContainsAscii(string path, string marker, long maximumLength = long.MaxValue)
    {
        if (string.IsNullOrEmpty(marker) || !File.Exists(path)) return false;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumLength) return false;
            return ContainsNeedle(stream, Encoding.ASCII.GetBytes(marker));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<bool> ContainsEach(string path, IReadOnlyList<IReadOnlyList<string>> markerGroups)
    {
        var absent = new bool[markerGroups.Count];
        if (markerGroups.Count == 0 || !File.Exists(path)) return absent;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ContainsEach(stream, markerGroups);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return absent;
        }
    }

    public static IReadOnlyList<bool> ContainsEach(Stream stream, IReadOnlyList<IReadOnlyList<string>> markerGroups)
    {
        var found = new bool[markerGroups.Count];
        var groups = markerGroups.Select(BuildNeedles).ToArray();
        var pending = 0;
        for (var index = 0; index < groups.Length; index++)
            if (groups[index].Length > 0) pending++;
        if (pending == 0) return found;

        var overlap = groups.SelectMany(group => group).Max(needle => needle.Length) - 1;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + overlap);
        try
        {
            var carried = 0;
            while (pending > 0)
            {
                var read = stream.ReadAtLeast(buffer.AsSpan(carried, ChunkSize), ChunkSize, throwOnEndOfStream: false);
                if (read == 0) break;
                FoldAscii(buffer.AsSpan(carried, read));
                var window = buffer.AsSpan(0, carried + read);
                for (var index = 0; index < groups.Length; index++)
                {
                    if (found[index] || groups[index].Length == 0) continue;
                    if (!ContainsAnyNeedle(window, groups[index])) continue;
                    found[index] = true;
                    pending--;
                }

                if (overlap <= 0)
                {
                    carried = 0;
                    continue;
                }

                var keep = Math.Min(overlap, window.Length);
                window[^keep..].CopyTo(buffer);
                carried = keep;
            }

            return found;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[][] BuildNeedles(IReadOnlyList<string> markers)
    {
        var needles = new List<byte[]>(markers.Count * 2);
        foreach (var marker in markers)
        {
            if (string.IsNullOrEmpty(marker)) continue;
            Add(Encoding.ASCII.GetBytes(marker));
            Add(Encoding.Unicode.GetBytes(marker));
        }

        return needles.ToArray();

        void Add(byte[] needle)
        {
            if (needle.Length == 0) return;
            FoldAscii(needle);
            if (!needles.Any(existing => existing.AsSpan().SequenceEqual(needle))) needles.Add(needle);
        }
    }

    private static bool ContainsAnyNeedle(ReadOnlySpan<byte> haystack, byte[][] needles)
    {
        foreach (var needle in needles)
            if (haystack.IndexOf(needle) >= 0) return true;
        return false;
    }

    private static bool ContainsNeedle(Stream stream, ReadOnlySpan<byte> needle)
    {
        var overlap = needle.Length - 1;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + overlap);
        try
        {
            var carried = 0;
            while (true)
            {
                var read = stream.ReadAtLeast(
                    buffer.AsSpan(carried, ChunkSize),
                    ChunkSize,
                    throwOnEndOfStream: false);
                if (read == 0) return false;

                var window = buffer.AsSpan(0, carried + read);
                if (window.IndexOf(needle) >= 0) return true;

                var keep = Math.Min(overlap, window.Length);
                window[^keep..].CopyTo(buffer);
                carried = keep;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void FoldAscii(Span<byte> value)
    {
        for (var index = 0; index < value.Length; index++)
            if (value[index] is >= (byte)'A' and <= (byte)'Z') value[index] += 32;
    }
}
