using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed record SourceDocumentEntry(
    string DocumentId,
    string ProviderId,
    string RootId,
    string CanonicalPath,
    long Size,
    long MtimeUtcTicks,
    long? Device,
    long? Inode,
    string FastFingerprint,
    int ParserVersion,
    long LastSuccessfulParseGeneration,
    bool IsStale)
{
    public static SourceDocumentEntry FromPath(
        string providerId,
        string rootId,
        string path,
        int parserVersion,
        long generation,
        bool stale = false)
    {
        var canonical = GameIdentity.NormalizePath(path);
        long size = 0;
        long mtime = 0;
        try
        {
            if (File.Exists(canonical))
            {
                var info = new FileInfo(canonical);
                size = info.Length;
                mtime = info.LastWriteTimeUtc.Ticks;
            }
            else if (Directory.Exists(canonical))
            {
                mtime = Directory.GetLastWriteTimeUtc(canonical).Ticks;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stale = true;
        }

        var fingerprint = StableHash.Hex(StableHash.Combine(
            StableHash.Combine(StableHash.Ordinal(canonical), size),
            mtime));
        var id = StableHash.Hex(StableHash.Combine(
            StableHash.Combine(StableHash.Ordinal(providerId), StableHash.Ordinal(rootId)),
            StableHash.Ordinal(canonical)));
        return new(id, providerId, rootId, canonical, size, mtime, null, null,
            fingerprint, parserVersion, generation, stale);
    }
}

public sealed record GameSourceRelationship(
    GameInstallId InstallId,
    string ProviderId,
    string ExternalId,
    string MetadataPath,
    int SourcePriority,
    DetectionConfidence Confidence,
    IReadOnlyList<FieldSelectionEvidence> SelectedFieldEvidence,
    long ReconciliationGeneration);

public sealed record PersistedGameAnalysis(
    GameInstallId InstallId,
    int AnalyzerVersion,
    string InputFingerprint,
    string? SelectedExecutable,
    IReadOnlyList<ExecutableCandidate> CandidateExecutables,
    GameEngine Engine,
    GameFingerprint? Fingerprint,
    int FilesVisited,
    int DirectoriesVisited,
    long DurationMilliseconds,
    InstalledGame Game,
    long Generation);
