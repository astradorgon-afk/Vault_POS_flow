using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Pos.Domain.Common;

namespace Pos.Api.Common;

/// <summary>
/// Translates application results into HTTP responses.
/// </summary>
/// <remarks>
/// Endpoints never build responses by hand. Every failure becomes an RFC 9457
/// problem document carrying the stable error code and the correlation
/// identifier, and nothing else: no stack traces, no exception types, no SQL.
/// </remarks>
public static class ProblemDetailsMapping
{
    private const string TypeBase = "https://vaultflow.local/errors/";

    /// <summary>Maps a failure classification to an HTTP status code.</summary>
    /// <param name="type">The failure classification.</param>
    /// <returns>The status code.</returns>
    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.ConcurrencyConflict => StatusCodes.Status412PreconditionFailed,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorType.ApprovalRequired => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Converts a failed result into a problem response.</summary>
    /// <param name="result">The failed result.</param>
    /// <param name="correlationId">The correlation identifier for this request.</param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentException">The result was successful.</exception>
    public static IResult ToProblem(Result result, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            throw new ArgumentException("Only a failed result can be mapped to a problem.", nameof(result));
        }

        Error primary = result.Error;
        int status = StatusFor(primary.Type);

        ProblemDetails problem = new()
        {
            Type = TypeBase + primary.Code.Replace('.', '/'),
            Title = TitleFor(primary.Type),
            Status = status,

            // Internal server errors reveal nothing: the detail is in the log,
            // correlated by identifier.
            Detail = status == StatusCodes.Status500InternalServerError ? null : primary.Message,
        };

        problem.Extensions["errorCode"] = primary.Code;
        problem.Extensions["correlationId"] = correlationId.ToString("D", CultureInfo.InvariantCulture);

        if (primary.Metadata is { Count: > 0 } && status != StatusCodes.Status500InternalServerError)
        {
            problem.Extensions["meta"] = primary.Metadata;
        }

        if (result.Errors.Count > 1)
        {
            problem.Extensions["errors"] = result.Errors
                .Select(e => new { code = e.Code, message = e.Message })
                .ToArray();
        }

        return TypedResults.Problem(problem);
    }

    /// <summary>Converts a result into either its value or a problem response.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="correlationId">The correlation identifier for this request.</param>
    /// <returns>The HTTP result.</returns>
    public static IResult ToHttpResult<TValue>(Result<TValue> result, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : ToProblem(result, correlationId);
    }

    private static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "Validation failed",
        ErrorType.Unauthenticated => "Authentication required",
        ErrorType.Forbidden => "Not permitted",
        ErrorType.NotFound => "Not found",
        ErrorType.Conflict => "Conflict with current state",
        ErrorType.ConcurrencyConflict => "The record changed while you were editing it",
        ErrorType.Unavailable => "Not available in this context",
        ErrorType.ApprovalRequired => "Approval required",
        _ => "An unexpected error occurred",
    };
}
