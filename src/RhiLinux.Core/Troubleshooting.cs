using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhiLinux.Core;

public enum ApplicationLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public sealed record ApplicationLogEntry(
    DateTimeOffset Timestamp,
    ApplicationLogLevel Level,
    string Event,
    string Message,
    string? Code = null);

public interface IApplicationLog
{
    Task WriteAsync(ApplicationLogEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationLogEntry>> ReadRecentAsync(int maximum, CancellationToken cancellationToken = default);
}

public sealed class BoundedApplicationLog(string directory, long maximumBytes = 2 * 1024 * 1024) : IApplicationLog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string CurrentPath => Path.Combine(directory, "rhi-linux.log");
    private string PreviousPath => Path.Combine(directory, "rhi-linux.previous.log");

    public async Task WriteAsync(ApplicationLogEntry entry, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length >= maximumBytes)
            {
                if (File.Exists(PreviousPath)) File.Delete(PreviousPath);
                File.Move(CurrentPath, PreviousPath);
            }
            var sanitized = entry with
            {
                Event = Sanitize(entry.Event, 128),
                Message = Sanitize(entry.Message, 4096),
                Code = entry.Code is null ? null : Sanitize(entry.Code, 128)
            };
            await File.AppendAllTextAsync(CurrentPath,
                JsonSerializer.Serialize(sanitized, Options) + Environment.NewLine,
                Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ApplicationLogEntry>> ReadRecentAsync(
        int maximum,
        CancellationToken cancellationToken = default)
    {
        if (maximum <= 0) return [];
        var entries = new List<ApplicationLogEntry>();
        foreach (var path in new[] { PreviousPath, CurrentPath })
        {
            if (!File.Exists(path)) continue;
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ApplicationLogEntry>(line, Options);
                    if (entry is not null) entries.Add(entry);
                }
                catch (JsonException)
                {
                }
            }
        }
        return entries.OrderByDescending(entry => entry.Timestamp).Take(maximum).ToArray();
    }

    private static string Sanitize(string value, int maximum)
    {
        var clean = value.Replace("\0", string.Empty, StringComparison.Ordinal);
        return clean.Length > maximum ? clean[..maximum] : clean;
    }
}

public sealed record TroubleshootingContext(
    XdgPaths Paths,
    IReadOnlyList<LibraryProviderStatus> Providers,
    int GameCount,
    LibraryMetrics LastRefresh,
    int SqliteSchemaVersion,
    string DatabaseHealth,
    string WatcherStatus,
    string LastOperationResult,
    int PendingRecoveryCount,
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    string PackagingMode = "direct");

public interface ITroubleshootingReportService
{
    Task<string> CreateAsync(TroubleshootingContext context, CancellationToken cancellationToken = default);
    Task SaveAsync(string path, string report, CancellationToken cancellationToken = default);
}

public sealed partial class TroubleshootingReportService(IApplicationLog? log = null) : ITroubleshootingReportService
{
    public async Task<string> CreateAsync(
        TroubleshootingContext context,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Add(builder, "RHI Linux version", version);
        Add(builder, "Operating system", ReadOperatingSystem());
        Add(builder, "Kernel", RuntimeInformation.OSDescription);
        Add(builder, "Architecture", RuntimeInformation.OSArchitecture.ToString());
        Add(builder, ".NET runtime", RuntimeInformation.FrameworkDescription);
        Add(builder, "Desktop", SafeEnvironment("XDG_CURRENT_DESKTOP") ?? "unknown");
        Add(builder, "Packaging", context.PackagingMode);
        Add(builder, "Config path", PrivacyPath(context.Paths.AppConfigDirectory));
        Add(builder, "Data path", PrivacyPath(context.Paths.AppDataDirectory));
        Add(builder, "Cache path", PrivacyPath(context.Paths.AppCacheDirectory));
        Add(builder, "Games", context.GameCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(builder, "Library DB schema", context.SqliteSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(builder, "Library DB health", SanitizeText(context.DatabaseHealth));
        Add(builder, "Watcher status", SanitizeText(context.WatcherStatus));
        Add(builder, "Last operation", SanitizeText(context.LastOperationResult));
        Add(builder, "Recovery pending", context.PendingRecoveryCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(builder, "Last refresh", FormatMetrics(context.LastRefresh));
        builder.AppendLine().AppendLine("Providers");
        foreach (var provider in context.Providers.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            builder.Append("- ").Append(provider.DisplayName).Append(": ")
                .Append(provider.Enabled ? "enabled" : "disabled").Append(" · ")
                .Append(provider.Available ? "detected" : "not detected").Append(" · ")
                .Append(provider.RecordCount).AppendLine(" games");
        builder.AppendLine().AppendLine("Recent diagnostics");
        foreach (var diagnostic in context.Diagnostics.TakeLast(50))
            builder.Append("- [").Append(diagnostic.Severity).Append("] ")
                .Append(diagnostic.ProviderId).Append('/').Append(diagnostic.Code).Append(": ")
                .AppendLine(SanitizeText(diagnostic.Message));
        if (context.Diagnostics.Count == 0) builder.AppendLine("- None");
        if (log is not null)
        {
            builder.AppendLine().AppendLine("Recent log events");
            foreach (var entry in await log.ReadRecentAsync(30, cancellationToken).ConfigureAwait(false))
                builder.Append("- ").Append(entry.Timestamp.ToString("O")).Append(" [").Append(entry.Level)
                    .Append("] ").Append(entry.Event).Append(": ").AppendLine(SanitizeText(entry.Message));
        }
        return builder.ToString();
    }

    public static string PrivacyPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var full = Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(home) && GameOverrideValidator.IsContained(home, full))
        {
            var relative = Path.GetRelativePath(home, full);
            return relative == "." ? "~" : "~/" + relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        return full;
    }

    public async Task SaveAsync(string path, string report, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? throw new InvalidOperationException("Report path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, report, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, full, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ReadOperatingSystem()
    {
        try
        {
            if (!File.Exists("/etc/os-release")) return RuntimeInformation.OSDescription;
            var values = File.ReadLines("/etc/os-release")
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1].Trim().Trim('"'), StringComparer.Ordinal);
            return values.GetValueOrDefault("PRETTY_NAME") ?? RuntimeInformation.OSDescription;
        }
        catch (IOException)
        {
            return RuntimeInformation.OSDescription;
        }
    }

    private static string? SafeEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl) ? null : value;
    }

    private static string FormatMetrics(LibraryMetrics metrics) =>
        $"{metrics.Reason} · {metrics.TotalDuration.TotalMilliseconds:0} ms · " +
        $"{metrics.SourceDocumentsParsed} parsed · {metrics.CacheHits} cache hits · {metrics.CacheMisses} misses";

    private static string SanitizeText(string value)
    {
        var sanitized = TokenPattern().Replace(value, "$1=<redacted>");
        sanitized = HomePattern().Replace(sanitized, "~/");
        return sanitized.Length > 2048 ? sanitized[..2048] : sanitized;
    }

    private static void Add(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append(": ").AppendLine(SanitizeText(value));

    [GeneratedRegex("(?i)(token|secret|password|cookie|authorization)\\s*[=:]\\s*[^\\s,;]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("/home/[^/]+/")]
    private static partial Regex HomePattern();
}
