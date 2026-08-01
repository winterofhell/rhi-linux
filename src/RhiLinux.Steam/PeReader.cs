using RhiLinux.Core;

namespace RhiLinux.Steam;

public static class PeReader
{
    public static PeArchitecture ReadArchitecture(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return PeArchitecture.Unknown;
            stream.Position = 0x3c;
            var peOffset = reader.ReadUInt32();
            if (peOffset > stream.Length - 6) return PeArchitecture.Unknown;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return PeArchitecture.Unknown;
            return reader.ReadUInt16() switch
            {
                0x014c => PeArchitecture.X86,
                0x8664 => PeArchitecture.X64,
                0xaa64 => PeArchitecture.Arm64,
                _ => PeArchitecture.Unknown
            };
        }
        catch (IOException) { return PeArchitecture.Unknown; }
        catch (UnauthorizedAccessException) { return PeArchitecture.Unknown; }
    }

    public static bool IsValid(string path) => ReadArchitecture(path) != PeArchitecture.Unknown;
}
