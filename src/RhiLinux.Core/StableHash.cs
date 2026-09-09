namespace RhiLinux.Core;

public static class StableHash
{
    private const long OffsetBasis = unchecked((long)14695981039346656037UL);
    private const long Prime = 1099511628211L;

    public static long Combine(long hash, string value) => unchecked((hash * 31) + Ordinal(value));

    public static long Combine(long hash, long value) => unchecked((hash * 31) + value);

    public static long Ordinal(string value) => Fold(value, ignoreCase: false);

    public static long OrdinalIgnoreCase(string value) => Fold(value, ignoreCase: true);

    public static string Hex(long value) => unchecked((ulong)value).ToString("x16");

    private static long Fold(string value, bool ignoreCase)
    {
        var hash = OffsetBasis;
        unchecked
        {
            foreach (var current in value)
            {
                var character = ignoreCase ? char.ToUpperInvariant(current) : current;
                hash = (hash ^ (byte)character) * Prime;
                hash = (hash ^ (byte)(character >> 8)) * Prime;
            }
        }

        return hash;
    }
}
