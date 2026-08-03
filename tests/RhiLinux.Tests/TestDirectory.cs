namespace RhiLinux.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rhi-linux-tests", Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(Path); }
    public string Path { get; }
    public string Combine(params string[] parts) => parts.Aggregate(Path, System.IO.Path.Combine);
    public string Directory(params string[] parts) { var path = Combine(parts); System.IO.Directory.CreateDirectory(path); return path; }
    public string File(string relative, string contents)
    {
        var path = Combine(relative.Split('/')); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, contents); return path;
    }
    public string Pe(string relative, ushort machine = 0x8664, long size = 2 * 1024 * 1024)
    {
        var path = Combine(relative.Split('/')); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var bytes = new byte[512]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        using var stream = System.IO.File.Create(path); stream.Write(bytes); stream.SetLength(size); return path;
    }
    public string PeWithMarker(string relative, string marker, ushort machine = 0x8664)
    {
        var path = Pe(relative, machine);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Position = 0x100;
        stream.Write(System.Text.Encoding.ASCII.GetBytes(marker));
        return path;
    }

    public string PeWithLateMarker(string relative, string marker, long markerOffset = 3L * 1024 * 1024, ushort machine = 0x8664)
    {
        var path = Pe(relative, machine, Math.Max(markerOffset + marker.Length + 64, 4L * 1024 * 1024));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Position = markerOffset;
        stream.Write(System.Text.Encoding.ASCII.GetBytes(marker));
        return path;
    }
    public void Dispose() { try { System.IO.Directory.Delete(Path, true); } catch (IOException) { } }
}
