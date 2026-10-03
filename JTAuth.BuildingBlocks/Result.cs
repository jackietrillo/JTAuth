namespace JTAuth.BuildingBlocks;

public enum ResultErrorType
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    TooManyRequests,
    Unexpected
}

public sealed record ResultError(
    string Code,
    string Message,
    ResultErrorType Type,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null)
{
    public static ResultError Validation(string code, string message, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(code, message, ResultErrorType.Validation, errors);

    public static ResultError NotFound(string code, string message) => new(code, message, ResultErrorType.NotFound);

    public static ResultError Conflict(string code, string message) => new(code, message, ResultErrorType.Conflict);

    public static ResultError Unexpected(string code, string message) => new(code, message, ResultErrorType.Unexpected);
}

public sealed class Result<T>
{
    internal Result(T? value, ResultError? error, bool isSuccess)
    {
        Value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T? Value { get; }

    public ResultError? Error { get; }
}

public static class Result
{
    public static Result<T> Success<T>(T value) => new(value, null, true);

    public static Result<Unit> Success() => new(Unit.Value, null, true);

    public static Result<T> Failure<T>(ResultError error) =>
        new(default, error ?? throw new ArgumentNullException(nameof(error)), false);
}

/// <summary>The result value of a command that returns nothing.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}
