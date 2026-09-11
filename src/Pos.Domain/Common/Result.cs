namespace Pos.Domain.Common;

/// <summary>
/// The outcome of an operation that can fail for expected business reasons.
/// Expected failures are values; exceptions are reserved for defects and
/// infrastructure faults.
/// </summary>
public class Result
{
    /// <summary>Initializes a new instance of the <see cref="Result"/> class.</summary>
    /// <param name="isSuccess">Whether the operation succeeded.</param>
    /// <param name="errors">The failures, when unsuccessful.</param>
    protected Result(bool isSuccess, IReadOnlyList<Error> errors)
    {
        if (isSuccess && errors.Count > 0)
        {
            throw new ArgumentException("A successful result cannot carry errors.", nameof(errors));
        }

        if (!isSuccess && errors.Count == 0)
        {
            throw new ArgumentException("A failed result must carry at least one error.", nameof(errors));
        }

        IsSuccess = isSuccess;
        Errors = errors;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets a value indicating whether the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Gets the failures. Empty when successful.</summary>
    public IReadOnlyList<Error> Errors { get; }

    /// <summary>Gets the first failure, or <see cref="Error.None"/> when successful.</summary>
    public Error Error => Errors.Count > 0 ? Errors[0] : Common.Error.None;

    /// <summary>Creates a successful result.</summary>
    public static Result Success() => new(true, []);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The failure.</param>
    public static Result Failure(Error error) => new(false, [error]);

    /// <summary>Creates a failed result from several failures.</summary>
    /// <param name="errors">The failures.</param>
    public static Result Failure(IReadOnlyList<Error> errors) => new(false, errors);

    /// <summary>Creates a successful result carrying a value.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="value">The value.</param>
    public static Result<TValue> Success<TValue>(TValue value) => Result<TValue>.Success(value);

    /// <summary>Creates a failed result of a value-bearing type.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="error">The failure.</param>
    public static Result<TValue> Failure<TValue>(Error error) => Result<TValue>.Failure(error);
}

/// <summary>The outcome of an operation that produces a value when it succeeds.</summary>
/// <typeparam name="TValue">The produced value type.</typeparam>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(bool isSuccess, TValue? value, IReadOnlyList<Error> errors)
        : base(isSuccess, errors)
        => _value = value;

    /// <summary>Gets the produced value. Throws when the result is a failure.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure ({Error.Code}); Value is not available.");

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The produced value.</param>
    public static Result<TValue> Success(TValue value) => new(true, value, []);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The failure.</param>
    public static new Result<TValue> Failure(Error error) => new(false, default, [error]);

    /// <summary>Creates a failed result from several failures.</summary>
    /// <param name="errors">The failures.</param>
    public static new Result<TValue> Failure(IReadOnlyList<Error> errors) => new(false, default, errors);

    /// <summary>Projects a successful value, propagating failure unchanged.</summary>
    /// <typeparam name="TNext">The projected value type.</typeparam>
    /// <param name="map">The projection.</param>
    public Result<TNext> Map<TNext>(Func<TValue, TNext> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Result<TNext>.Success(map(Value)) : Result<TNext>.Failure(Errors);
    }

    /// <summary>Converts a value into a successful result.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator Result<TValue>(TValue value) => Success(value);
}
