using System.IO.Compression;
using System.Text;
using RhiLinux.Core;
using SharpCompress.Archives;

namespace RhiLinux.Mods;

public enum ArtifactFormat
{
    Unknown,
    DirectPeAddon,
    ZipContainer,
    SevenZipContainer,
    ReShadeInstallerZip,
    Unsupported
}

public enum ArtifactPipelineStage
{
    Resolve,
    Download,
    ValidateSource,
    DetectFormat,
    InspectContents,
    SelectPayload,
    Extract,
    ValidateExtracted,
    BuildPlan,
    ApplyTransaction,
    Configure,
    Verify,
    CommitManifest
}

public sealed class ArtifactPipelineException : Exception
{
    public ArtifactPipelineException(
        ComponentKind component,
        ArtifactPipelineStage stage,
        string message,
        bool gameFilesChanged = false,
        bool? rollbackSucceeded = null,
        string? technicalDetail = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Component = component;
        Stage = stage;
        GameFilesChanged = gameFilesChanged;
        RollbackSucceeded = rollbackSucceeded;
        TechnicalDetail = technicalDetail ?? innerException?.ToString();
    }

    public ComponentKind Component { get; }
    public ArtifactPipelineStage Stage { get; }
    public bool GameFilesChanged { get; }
    public bool? RollbackSucceeded { get; }
    public string? TechnicalDetail { get; }

    public string UserSummary =>
        $"{Component} could not be prepared during {Stage}. {Message}" +
        (GameFilesChanged
            ? RollbackSucceeded == true
                ? " Game file changes were rolled back."
                : RollbackSucceeded == false
                    ? " Game file changes may remain; review Advanced diagnostics."
                    : " Game files may have changed."
            : " No game files were changed.");
}

public sealed record ArchiveEntryInfo(
    string RelativePath,
    long UncompressedSize,
    bool IsDirectory,
    bool IsSymlink);

public sealed record ArchiveInspectionResult(
    ArtifactFormat Format,
    IReadOnlyList<ArchiveEntryInfo> Entries,
    long TotalUncompressedBytes,
    string? Warning);

public sealed record CapabilityPreflightReport(
    bool ManagedZipSupported,
    bool ManagedReShadeInstallerSupported,
    bool ManagedSevenZipSupported,
    bool CacheWritable,
    bool TemporaryWritable,
    bool ExternalBsdtarPresent,
    bool ExternalSevenZipPresent,
    string Summary)
{
    public bool RequiredCapabilitiesAvailable =>
        ManagedZipSupported && ManagedReShadeInstallerSupported && ManagedSevenZipSupported &&
        CacheWritable && TemporaryWritable;
}

public static class ManagedArchiveExtractor
{
    public const int MaximumEntryCount = 4096;
    public const long MaximumUncompressedBytes = ArtifactValidator.MaxDownloadSizeBytes;
    public const long MaximumCompressionRatio = 1000;

    public static CapabilityPreflightReport ProbeCapabilities(string cacheDirectory, string temporaryDirectory)
    {
        var cacheWritable = CanWriteDirectory(cacheDirectory);
        var temporaryWritable = CanWriteDirectory(temporaryDirectory);
        var zip = true;
        var reshade = true;
        var sevenZip = true;
        try
        {
            using var probe = new MemoryStream();
            using (var zipArchive = new System.IO.Compression.ZipArchive(probe, ZipArchiveMode.Create, true))
            {
                var entry = zipArchive.CreateEntry("probe.txt");
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes("ok"));
            }
            probe.Position = 0;
            using var read = new System.IO.Compression.ZipArchive(probe, ZipArchiveMode.Read);
            _ = read.Entries.Count;
        }
        catch (Exception)
        {
            zip = false;
            reshade = false;
        }

        try
        {
            using var empty = new MemoryStream(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
            _ = empty;
            _ = typeof(SharpCompress.Archives.SevenZip.SevenZipArchive);
        }
        catch (Exception)
        {
            sevenZip = false;
        }

        var bsdtar = FindOnPath("bsdtar") is not null;
        var sevenZipTool = FindOnPath("7z") is not null || FindOnPath("7za") is not null;
        var summary = RequiredCapabilitiesAvailable(zip, reshade, sevenZip, cacheWritable, temporaryWritable)
            ? "Managed archive inspection is available without external extractors."
            : "One or more required managed extraction capabilities are unavailable.";
        return new(zip, reshade, sevenZip, cacheWritable, temporaryWritable, bsdtar, sevenZipTool, summary);
    }

    private static bool RequiredCapabilitiesAvailable(
        bool zip, bool reshade, bool sevenZip, bool cache, bool temporary) =>
        zip && reshade && sevenZip && cache && temporary;

    public static ArtifactFormat DetectFormat(string path, ArtifactArchiveKind kindHint)
    {
        if (kindHint == ArtifactArchiveKind.None)
            return ArtifactFormat.DirectPeAddon;
        if (!File.Exists(path)) return ArtifactFormat.Unknown;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        var read = stream.Read(header);
        if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B)
            return kindHint == ArtifactArchiveKind.ReShadeInstaller
                ? ArtifactFormat.ReShadeInstallerZip
                : ArtifactFormat.ZipContainer;
        if (read >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC &&
            header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
            return ArtifactFormat.SevenZipContainer;
        if (read >= 2 && header[0] == 0x4D && header[1] == 0x5A)
        {
            if (kindHint == ArtifactArchiveKind.ReShadeInstaller && TryOpenReShadeInstallerZip(path, out var probe))
            {
                probe.Dispose();
                return ArtifactFormat.ReShadeInstallerZip;
            }
            return kindHint == ArtifactArchiveKind.ReShadeInstaller
                ? ArtifactFormat.Unknown
                : ArtifactFormat.DirectPeAddon;
        }
        return ArtifactFormat.Unsupported;
    }

    public static ArchiveInspectionResult Inspect(string archivePath, ArtifactFormat format)
    {
        return format switch
        {
            ArtifactFormat.ZipContainer or ArtifactFormat.ReShadeInstallerZip => InspectZip(archivePath, format),
            ArtifactFormat.SevenZipContainer => InspectSevenZip(archivePath),
            _ => throw new ArtifactPipelineException(ComponentKind.OptiScaler, ArtifactPipelineStage.DetectFormat,
                "This release uses an archive format that this version of RHI Linux cannot inspect safely.")
        };
    }

    public static async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        ArtifactFormat format,
        IReadOnlyCollection<string>? selectedFileNames,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        switch (format)
        {
            case ArtifactFormat.ZipContainer:
            case ArtifactFormat.ReShadeInstallerZip:
                await ExtractZipAsync(archivePath, destinationDirectory, format, selectedFileNames, cancellationToken);
                break;
            case ArtifactFormat.SevenZipContainer:
                await ExtractSevenZipAsync(archivePath, destinationDirectory, selectedFileNames, cancellationToken);
                break;
            default:
                throw new ArtifactPipelineException(ComponentKind.OptiScaler, ArtifactPipelineStage.Extract,
                    "This release uses an archive format that this version of RHI Linux cannot inspect safely.");
        }
        ValidateExtractedTree(destinationDirectory);
    }

    public static async Task ExtractSingleNamedFileAsync(
        string archivePath,
        ArtifactFormat format,
        string entryFileName,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(outputPath)!, $".extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            await ExtractAsync(archivePath, temporary, format, [entryFileName], cancellationToken);
            var matches = Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Equals(entryFileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"The archive does not contain exactly one '{entryFileName}' payload.");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(matches[0], outputPath, true);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    private static ArchiveInspectionResult InspectZip(string path, ArtifactFormat format)
    {
        using var stream = OpenZipStream(path, format);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = new List<ArchiveEntryInfo>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var relative = NormalizeArchivePath(entry.FullName);
            ValidateRelativePath(relative);
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            total += entry.Length;
            if (entries.Count >= MaximumEntryCount)
                throw new InvalidDataException("Archive contains too many entries.");
            if (total > MaximumUncompressedBytes)
                throw new InvalidDataException("Archive expands beyond the 2 GiB safety limit.");
            entries.Add(new(relative, entry.Length, isDirectory, false));
        }
        RejectCollisions(entries);
        return new(format, entries, total, null);
    }

    private static ArchiveInspectionResult InspectSevenZip(string path)
    {
        using var archive = ArchiveFactory.Open(path);
        var entries = new List<ArchiveEntryInfo>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var key = entry.Key ?? string.Empty;
            var relative = NormalizeArchivePath(key);
            ValidateRelativePath(relative);
            total += entry.Size;
            if (entries.Count >= MaximumEntryCount)
                throw new InvalidDataException("Archive contains too many entries.");
            if (total > MaximumUncompressedBytes)
                throw new InvalidDataException("Archive expands beyond the 2 GiB safety limit.");
            if (entry.IsEncrypted)
                throw new InvalidDataException("Encrypted archive entries are not supported.");
            entries.Add(new(relative, entry.Size, entry.IsDirectory, entry.LinkTarget is not null));
        }
        if (entries.Any(item => item.IsSymlink))
            throw new InvalidDataException("Archive contains a symbolic link entry.");
        RejectCollisions(entries);
        return new(ArtifactFormat.SevenZipContainer, entries, total, null);
    }

    private static async Task ExtractZipAsync(
        string archivePath,
        string destinationDirectory,
        ArtifactFormat format,
        IReadOnlyCollection<string>? selectedFileNames,
        CancellationToken cancellationToken)
    {
        var inspection = InspectZip(archivePath, format);
        GuardCompressionRatio(Math.Max(1, new FileInfo(archivePath).Length), inspection.TotalUncompressedBytes);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory)) + Path.DirectorySeparatorChar;
        await using var stream = OpenZipStream(archivePath, format);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeArchivePath(entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                var directory = Path.GetFullPath(Path.Combine(destinationDirectory, relative));
                EnsureContained(root, directory);
                Directory.CreateDirectory(directory);
                continue;
            }
            if (selectedFileNames is not null &&
                !selectedFileNames.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase))
                continue;
            var target = Path.GetFullPath(Path.Combine(destinationDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureContained(root, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await ArtifactValidator.CopyWithLimitAsync(input, output, MaximumUncompressedBytes, cancellationToken);
        }
    }

    internal static Stream OpenZipStream(string path, ArtifactFormat format)
    {
        if (format == ArtifactFormat.ReShadeInstallerZip && TryOpenReShadeInstallerZip(path, out var installer))
            return installer;
        return File.OpenRead(path);
    }

    private static bool TryOpenReShadeInstallerZip(string path, out Stream stream)
    {
        stream = Stream.Null;
        Stream? candidate = null;
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[2];
            if (file.Read(header) < 2) return false;
            if (header[0] == 0x50 && header[1] == 0x4B)
            {
                candidate = File.OpenRead(path);
            }
            else
            {
                if (header[0] != 0x4D || header[1] != 0x5A) return false;
                var overlay = FindPeOverlayOffset(file);
                if (overlay <= 0 || overlay >= file.Length) return false;
                var length = file.Length - overlay;
                if (length < 22 || length > MaximumUncompressedBytes) return false;
                var payload = new byte[length];
                file.Position = overlay;
                var read = 0;
                while (read < payload.Length)
                {
                    var n = file.Read(payload, read, payload.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read < 4 || payload[0] != 0x50 || payload[1] != 0x4B) return false;
                candidate = new MemoryStream(payload, 0, read, writable: false, publiclyVisible: true);
            }

            using (var probe = new ZipArchive(candidate, ZipArchiveMode.Read, leaveOpen: true))
                _ = probe.Entries.Count;
            candidate.Position = 0;
            stream = candidate;
            candidate = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
                                               or NotSupportedException or ArgumentException)
        {
            candidate?.Dispose();
            stream = Stream.Null;
            return false;
        }
    }

    internal static long FindPeOverlayOffset(Stream stream)
    {
        if (!stream.CanSeek) throw new InvalidDataException("PE inspection requires a seekable stream.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var origin = stream.Position;
        stream.Position = 0;
        if (reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Artifact does not have an MZ header.");
        stream.Position = 0x3C;
        var peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 24) throw new InvalidDataException("Artifact has an invalid PE offset.");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Artifact does not have a PE signature.");
        stream.Position = peOffset + 6;
        var sectionCount = reader.ReadUInt16();
        stream.Position = peOffset + 20;
        var optionalHeaderSize = reader.ReadUInt16();
        var sectionTable = peOffset + 24 + optionalHeaderSize;
        if (sectionTable + (uint)sectionCount * 40 > stream.Length)
            throw new InvalidDataException("Artifact has an invalid PE section table.");
        long end = 0;
        for (var i = 0; i < sectionCount; i++)
        {
            stream.Position = sectionTable + (uint)(i * 40) + 16;
            var sizeOfRawData = reader.ReadUInt32();
            var pointerToRawData = reader.ReadUInt32();
            if (pointerToRawData == 0 || sizeOfRawData == 0) continue;
            end = Math.Max(end, pointerToRawData + (long)sizeOfRawData);
        }
        stream.Position = origin;
        if (end <= 0 || end > stream.Length) throw new InvalidDataException("Artifact has no PE overlay payload.");
        return end;
    }

    private static async Task ExtractSevenZipAsync(
        string archivePath,
        string destinationDirectory,
        IReadOnlyCollection<string>? selectedFileNames,
        CancellationToken cancellationToken)
    {
        var inspection = InspectSevenZip(archivePath);
        GuardCompressionRatio(new FileInfo(archivePath).Length, inspection.TotalUncompressedBytes);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory)) + Path.DirectorySeparatorChar;
        using var archive = ArchiveFactory.Open(archivePath);
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeArchivePath(reader.Entry.Key ?? string.Empty);
            ValidateRelativePath(relative);
            if (reader.Entry.IsDirectory)
            {
                var directory = Path.GetFullPath(Path.Combine(destinationDirectory, relative));
                EnsureContained(root, directory);
                Directory.CreateDirectory(directory);
                continue;
            }
            if (reader.Entry.LinkTarget is not null)
                throw new InvalidDataException("Archive contains a symbolic link entry.");
            if (selectedFileNames is not null &&
                !selectedFileNames.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            var target = Path.GetFullPath(Path.Combine(destinationDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureContained(root, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            reader.WriteEntryTo(output);
            await output.FlushAsync(cancellationToken);
        }
    }

    private static void GuardCompressionRatio(long compressedBytes, long uncompressedBytes)
    {
        if (compressedBytes < 4096) return;
        if (uncompressedBytes / Math.Max(1, compressedBytes) > MaximumCompressionRatio)
            throw new InvalidDataException("Archive compression ratio exceeds the safety limit.");
    }

    private static void RejectCollisions(IReadOnlyList<ArchiveEntryInfo> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(item => !item.IsDirectory))
        {
            if (!seen.Add(entry.RelativePath.Replace('\\', '/')))
                throw new InvalidDataException($"Archive contains duplicate destination '{entry.RelativePath}'.");
        }
    }

    public static string NormalizeArchivePath(string path)
    {
        var raw = path.Replace('\\', '/').Trim();
        if (raw.StartsWith('/') || raw.StartsWith("//", StringComparison.Ordinal) ||
            raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':')
            throw new InvalidDataException($"Archive entry uses an absolute or drive path: {path}");
        while (raw.StartsWith("./", StringComparison.Ordinal)) raw = raw[2..];
        return raw.TrimStart('/');
    }

    public static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException("Archive contains an empty entry path.");
        if (relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException($"Archive entry escapes the extraction root: {relative}");
        if (relative.Length >= 2 && char.IsLetter(relative[0]) && relative[1] == ':')
            throw new InvalidDataException($"Archive entry uses a Windows drive path: {relative}");
        if (relative.StartsWith("//", StringComparison.Ordinal) || relative.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException($"Archive entry uses a UNC path: {relative}");
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.Contains('\0')))
            throw new InvalidDataException($"Archive entry escapes the extraction root: {relative}");
    }

    private static void EnsureContained(string rootWithSeparator, string candidate)
    {
        var full = Path.GetFullPath(candidate);
        if (!full.Equals(Path.TrimEndingDirectorySeparator(rootWithSeparator), StringComparison.Ordinal) &&
            !full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive entry escapes the destination: {candidate}");
    }

    public static void ValidateExtractedTree(string destination)
    {
        long totalSize = 0;
        var count = 0;
        var directories = new Stack<string>();
        directories.Push(destination);
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > MaximumEntryCount)
                    throw new InvalidDataException("Archive contains too many extracted entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Archive contains a symbolic link or reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }
                totalSize += new FileInfo(entry).Length;
                if (totalSize > MaximumUncompressedBytes)
                    throw new InvalidDataException("Archive expands beyond the 2 GiB safety limit.");
            }
        }
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
