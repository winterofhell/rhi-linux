namespace RhiLinux.Core;

public sealed record OperationError(
    string Code,
    string Message,
    string? TechnicalDetails = null,
    string? AffectedPath = null,
    string? AffectedComponent = null,
    bool Retryable = false,
    string? SuggestedAction = null);

public sealed record OperationWarning(
    string Code,
    string Message,
    string? AffectedPath = null);

public sealed record DiagnosticEvent(
    string Category,
    string Message,
    string? Path = null,
    DateTimeOffset? TimestampUtc = null);

public sealed record OperationResult<T>(
    bool IsSuccess,
    T? Value,
    OperationError? Error,
    IReadOnlyList<OperationWarning> Warnings,
    IReadOnlyList<DiagnosticEvent> Diagnostics)
{
    public static OperationResult<T> Success(
        T value,
        IReadOnlyList<OperationWarning>? warnings = null,
        IReadOnlyList<DiagnosticEvent>? diagnostics = null) =>
        new(true, value, null, warnings ?? [], diagnostics ?? []);

    public static OperationResult<T> Failure(
        OperationError error,
        IReadOnlyList<OperationWarning>? warnings = null,
        IReadOnlyList<DiagnosticEvent>? diagnostics = null) =>
        new(false, default, error, warnings ?? [], diagnostics ?? []);
}
