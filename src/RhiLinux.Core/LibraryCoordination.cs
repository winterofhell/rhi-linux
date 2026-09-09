using System.Security.Cryptography;
using System.Text.Json;

namespace RhiLinux.Core;

public sealed record LibraryRefreshScope(
    IReadOnlySet<string>? Providers = null,
    IReadOnlySet<string>? RootIds = null,
    IReadOnlySet<string>? Documents = null,
    bool Force = false,
    string Reason = "Requested refresh")
{
    public static LibraryRefreshScope Full(string reason = "Full library refresh", bool force = false) =>
        new(Force: force, Reason: reason);

    public bool IncludesProvider(string providerId) =>
        Providers is null || Providers.Count == 0 || Providers.Contains(providerId);

    public bool IncludesRoot(string rootId) =>
        RootIds is null || RootIds.Count == 0 || RootIds.Contains(rootId);

    public bool IncludesDocument(string path) =>
        Documents is null || Documents.Count == 0 || Documents.Contains(GameIdentity.NormalizePath(path));

    public LibraryRefreshScope Merge(LibraryRefreshScope other) => new(
        MergeSets(Providers, other.Providers),
        MergeSets(RootIds, other.RootIds),
        MergeSets(Documents, other.Documents, normalizePaths: true),
        Force || other.Force,
        string.Equals(Reason, other.Reason, StringComparison.Ordinal)
            ? Reason
            : $"{Reason}; {other.Reason}");

    private static IReadOnlySet<string>? MergeSets(
        IReadOnlySet<string>? left,
        IReadOnlySet<string>? right,
        bool normalizePaths = false)
    {
        if (left is null || left.Count == 0 || right is null || right.Count == 0) return null;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in left.Concat(right))
            result.Add(normalizePaths ? GameIdentity.NormalizePath(value) : value);
        return result;
    }
}

public sealed record LibraryMetrics(
    string Reason,
    LibraryRefreshScope Scope,
    IReadOnlyList<string> ProvidersConsidered,
    IReadOnlyList<string> ProvidersExecuted,
    int SourceRootsInspected,
    int SourceDocumentsStatted,
    int SourceDocumentsParsed,
    int SourceRecordsAdded,
    int SourceRecordsChanged,
    int SourceRecordsRemoved,
    int InstallationsReconciled,
    int InstallationsAnalyzed,
    int LibraryRowsAdded,
    int LibraryRowsUpdated,
    int LibraryRowsRemoved,
    int CacheHits,
    int CacheMisses,
    int DirectoryTraversals,
    TimeSpan SqliteReadDuration,
    TimeSpan SqliteWriteDuration,
    TimeSpan TotalDuration)
{
    public static LibraryMetrics Empty { get; } = new(
        Reason: "No refresh yet",
        Scope: LibraryRefreshScope.Full("No refresh yet"),
        ProvidersConsidered: [],
        ProvidersExecuted: [],
        SourceRootsInspected: 0,
        SourceDocumentsStatted: 0,
        SourceDocumentsParsed: 0,
        SourceRecordsAdded: 0,
        SourceRecordsChanged: 0,
        SourceRecordsRemoved: 0,
        InstallationsReconciled: 0,
        InstallationsAnalyzed: 0,
        LibraryRowsAdded: 0,
        LibraryRowsUpdated: 0,
        LibraryRowsRemoved: 0,
        CacheHits: 0,
        CacheMisses: 0,
        DirectoryTraversals: 0,
        SqliteReadDuration: TimeSpan.Zero,
        SqliteWriteDuration: TimeSpan.Zero,
        TotalDuration: TimeSpan.Zero);
}

public sealed record LibraryProviderStatus(
    string ProviderId,
    string DisplayName,
    bool Enabled,
    bool Available,
    bool Stale,
    int RootCount,
    int RecordCount,
    string Summary);

public sealed record LibrarySnapshot(
    long Generation,
    IReadOnlyList<InstalledGame> Games,
    IReadOnlyDictionary<GameInstallId, InstalledGame> ById,
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    IReadOnlyList<LibraryProviderStatus> Providers,
    LibraryMetrics Metrics,
    DateTimeOffset UpdatedAt)
{
    public static LibrarySnapshot Empty { get; } = new(
        0,
        [],
        new Dictionary<GameInstallId, InstalledGame>(),
        [],
        [],
        LibraryMetrics.Empty,
        DateTimeOffset.MinValue);

    public static LibrarySnapshot Create(
        long generation,
        IEnumerable<InstalledGame> games,
        IEnumerable<SourceDiagnostic> diagnostics,
        IEnumerable<LibraryProviderStatus> providers,
        LibraryMetrics metrics,
        DateTimeOffset? updatedAt = null)
    {
        var ordered = games
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.InstallId)
            .ToArray();
        return new(
            generation,
            ordered,
            ordered.ToDictionary(game => game.InstallId),
            diagnostics.ToArray(),
            providers.OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
            metrics,
            updatedAt ?? DateTimeOffset.UtcNow);
    }
}

public sealed record LibraryChangeSet(
    IReadOnlyList<GameInstallId> Added,
    IReadOnlyList<GameInstallId> Updated,
    IReadOnlyList<GameInstallId> Removed,
    IReadOnlyList<GameInstallId> Stale,
    bool DiagnosticsChanged,
    bool ProviderStatusChanged)
{
    public bool IsEmpty => Added.Count == 0 && Updated.Count == 0 && Removed.Count == 0 &&
                           Stale.Count == 0 && !DiagnosticsChanged && !ProviderStatusChanged;

    public static LibraryChangeSet Between(LibrarySnapshot previous, LibrarySnapshot current)
    {
        var added = current.ById.Keys.Except(previous.ById.Keys).Order().ToArray();
        var removed = previous.ById.Keys.Except(current.ById.Keys).Order().ToArray();
        var updated = current.ById
            .Where(pair => previous.ById.TryGetValue(pair.Key, out var old) &&
                           Fingerprint(old) != Fingerprint(pair.Value))
            .Select(pair => pair.Key)
            .Order()
            .ToArray();
        var stale = current.Games.Where(game => game.IsStale).Select(game => game.InstallId).Order().ToArray();
        return new(
            added,
            updated,
            removed,
            stale,
            !previous.Diagnostics.SequenceEqual(current.Diagnostics),
            !previous.Providers.SequenceEqual(current.Providers));
    }

    private static string Fingerprint(InstalledGame game) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(game)));
}

public sealed record LibraryRefreshResult(
    LibrarySnapshot Snapshot,
    LibraryChangeSet Changes,
    LibraryMetrics Metrics,
    bool Published);

public sealed class LibraryChangedEventArgs(
    LibrarySnapshot previous,
    LibrarySnapshot current,
    LibraryChangeSet changes) : EventArgs
{
    public LibrarySnapshot Previous { get; } = previous;
    public LibrarySnapshot Current { get; } = current;
    public LibraryChangeSet Changes { get; } = changes;
}

public interface ILibraryCoordinator : IAsyncDisposable
{
    LibrarySnapshot Current { get; }
    event EventHandler<LibraryChangedEventArgs>? Changed;
    Task<LibrarySnapshot> LoadCachedAsync(CancellationToken cancellationToken = default);
    Task<LibraryRefreshResult> RefreshAsync(
        LibraryRefreshScope scope,
        CancellationToken cancellationToken = default);
}
