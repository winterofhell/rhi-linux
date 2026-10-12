using System.IO.Compression;
using SharpCompress.Archives;

namespace RhiLinux.Mods;

public static class ShaderArchiveStager
{
    public const string ShadersDirectoryName = "Shaders";
    public const string TexturesDirectoryName = "Textures";
    public const int MaximumEntries = 65536;
    public const long MaximumExtractedBytes = 1024L * 1024 * 1024;
    public const long MaximumEntryBytes = 256L * 1024 * 1024;

    private static readonly HashSet<string> ExcludedShaderFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "BX_XIV_ChromakeyPlus.fx", "GrainSpread.fx", "NTSCCustom.fx", "NTSC_XOT.fx"
    };

    private static readonly string[] FrameworkHeaders = ["ReShade.fxh", "ReShadeUI.fxh"];

    public static async Task<int> StageAsync(
        string archivePath,
        string packId,
        string treeRoot,
        CancellationToken cancellationToken = default)
    {
        if (packId.Length == 0 || packId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || packId.Contains(".."))
            throw new ArgumentException("The shader pack identifier is not a valid directory name.", nameof(packId));
        var format = ManagedArchiveExtractor.DetectFormat(archivePath, ArtifactArchiveKind.Zip);
        var count = format switch
        {
            ArtifactFormat.ZipContainer => await StageZipAsync(archivePath, packId, treeRoot, cancellationToken),
            ArtifactFormat.SevenZipContainer => await StageSevenZipAsync(archivePath, packId, treeRoot, cancellationToken),
            _ => throw new InvalidDataException($"The {packId} download is not a ZIP or 7z archive.")
        };
        foreach (var header in FrameworkHeaders)
        {
            var packHeader = Path.Combine(treeRoot, ShadersDirectoryName, packId, header);
            var rootHeader = Path.Combine(treeRoot, ShadersDirectoryName, header);
            if (File.Exists(packHeader) && (!File.Exists(rootHeader) || packId.Equals("CrosireMaster", StringComparison.Ordinal)))
                File.Copy(packHeader, rootHeader, true);
        }
        return count;
    }

    public static string? MapEntry(string entryKey, string packId)
    {
        var key = entryKey.Replace('\\', '/');
        if (key.EndsWith('/')) return null;
        string? root = null;
        string? relative = null;
        foreach (var token in new[] { ShadersDirectoryName + "/", TexturesDirectoryName + "/" })
        {
            var index = key.IndexOf("/" + token, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                root = token[..^1];
                relative = key[(index + 1 + token.Length)..];
                break;
            }
            if (key.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                root = token[..^1];
                relative = key[token.Length..];
                break;
            }
        }
        if (root is null || string.IsNullOrEmpty(relative))
        {
            var extension = Path.GetExtension(key);
            if (!extension.Equals(".fx", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".fxh", StringComparison.OrdinalIgnoreCase))
                return null;
            root = ShadersDirectoryName;
            var slash = key.IndexOf('/');
            relative = slash >= 0 ? key[(slash + 1)..] : key;
        }
        if (relative.Length == 0) return null;
        if (root == ShadersDirectoryName && ExcludedShaderFiles.Contains(Path.GetFileName(relative))) return null;
        var normalized = ManagedArchiveExtractor.NormalizeArchivePath(relative);
        ManagedArchiveExtractor.ValidateRelativePath(normalized);
        return Path.Combine(root, packId, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    private static async Task<int> StageZipAsync(string archivePath, string packId, string treeRoot, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > MaximumEntries) throw new InvalidDataException($"The {packId} archive contains too many entries.");
        var budget = new ExtractionBudget(packId);
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = MapEntry(entry.FullName, packId);
            if (relative is null) continue;
            budget.Reserve(entry.Length, entry.CompressedLength);
            await using var input = entry.Open();
            await WriteEntryAsync(input, treeRoot, relative, cancellationToken);
            count++;
        }
        return count;
    }

    private static async Task<int> StageSevenZipAsync(string archivePath, string packId, string treeRoot, CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        if (archive.Entries.Count() > MaximumEntries) throw new InvalidDataException($"The {packId} archive contains too many entries.");
        var budget = new ExtractionBudget(packId);
        var count = 0;
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory || entry.Key is null) continue;
            if (entry.LinkTarget is not null) throw new InvalidDataException($"The {packId} archive contains a symbolic link.");
            var relative = MapEntry(entry.Key, packId);
            if (relative is null) continue;
            budget.Reserve(entry.Size, entry.CompressedSize);
            await using var input = reader.OpenEntryStream();
            await WriteEntryAsync(input, treeRoot, relative, cancellationToken);
            count++;
        }
        return count;
    }

    private static async Task WriteEntryAsync(Stream input, string treeRoot, string relative, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(treeRoot)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(treeRoot, relative));
        if (!target.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive entry escapes the shader directory: {relative}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await ArtifactValidator.CopyWithLimitAsync(input, output, MaximumEntryBytes, cancellationToken);
    }

    private sealed class ExtractionBudget(string packId)
    {
        private long total;

        public void Reserve(long size, long compressedSize)
        {
            if (size > MaximumEntryBytes) throw new InvalidDataException($"A file in the {packId} archive exceeds the size limit.");
            if (compressedSize > 0 && size / compressedSize > ManagedArchiveExtractor.MaximumCompressionRatio)
                throw new InvalidDataException($"The {packId} archive has a suspicious compression ratio.");
            total += Math.Max(size, 0);
            if (total > MaximumExtractedBytes) throw new InvalidDataException($"The {packId} archive expands beyond the 1 GiB limit.");
        }
    }
}
