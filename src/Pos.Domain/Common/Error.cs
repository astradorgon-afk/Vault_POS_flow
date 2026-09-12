namespace Pos.Domain.Common;

/// <summary>
/// Classifies a failure so transport layers can map it without string matching.
/// </summary>
public enum ErrorType
{
    /// <summary>Input failed validation. Maps to HTTP 400.</summary>
    Validation = 0,

    /// <summary>The caller is not permitted. Maps to HTTP 403.</summary>
    Forbidden = 1,

    /// <summary>The caller is not authenticated. Maps to HTTP 401.</summary>
    Unauthenticated = 2,

    /// <summary>The referenced record does not exist. Maps to HTTP 404.</summary>
    NotFound = 3,

    /// <summary>The action conflicts with current state. Maps to HTTP 409.</summary>
    Conflict = 4,

    /// <summary>An optimistic concurrency check failed. Maps to HTTP 412.</summary>
    ConcurrencyConflict = 5,

    /// <summary>The action is not available in this context (for example, offline).</summary>
    Unavailable = 6,

    /// <summary>The action needs an approval that has not been granted.</summary>
    ApprovalRequired = 7,

    /// <summary>An unexpected fault. Maps to HTTP 500 with no detail.</summary>
    Unexpected = 8,
}

/// <summary>
/// A failure with a stable, machine-readable code. The code is part of the API
/// contract; the message is for humans and may change.
/// </summary>
/// <param name="Code">Stable dotted code, for example <c>inventory.insufficient_stock</c>.</param>
/// <param name="Message">Human-readable description, safe to show to a user.</param>
/// <param name="Type">The failure classification.</param>
/// <param name="Metadata">Optional structured detail (quantities, ids, field names).</param>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type = ErrorType.Validation,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    /// <summary>The absence of an error.</summary>
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Unexpected);

    /// <summary>Creates a validation error.</summary>
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, object?>? metadata = null)
        => new(code, message, ErrorType.Validation, metadata);

    /// <summary>Creates a not-found error.</summary>
    public static Error NotFound(string code, string message)
        => new(code, message, ErrorType.NotFound);

    /// <summary>Creates a conflict error.</summary>
    public static Error Conflict(string code, string message, IReadOnlyDictionary<string, object?>? metadata = null)
        => new(code, message, ErrorType.Conflict, metadata);

    /// <summary>Creates an authorization error.</summary>
    public static Error Forbidden(string code, string message)
        => new(code, message, ErrorType.Forbidden);

    /// <summary>Creates an optimistic-concurrency error. Maps to HTTP 412.</summary>
    public static Error ConcurrencyConflict(string code, string message, IReadOnlyDictionary<string, object?>? metadata = null)
        => new(code, message, ErrorType.ConcurrencyConflict, metadata);

    /// <summary>Creates an unavailable-in-this-context error. Maps to HTTP 503.</summary>
    public static Error Unavailable(string code, string message, IReadOnlyDictionary<string, object?>? metadata = null)
        => new(code, message, ErrorType.Unavailable, metadata);

    /// <summary>Creates an approval-required error.</summary>
    public static Error ApprovalRequired(string code, string message, IReadOnlyDictionary<string, object?>? metadata = null)
        => new(code, message, ErrorType.ApprovalRequired, metadata);

    /// <inheritdoc />
    public override string ToString() => $"{Code}: {Message}";
}
