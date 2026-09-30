namespace AIEnabledRma.Domain.Common;

/// <summary>
/// Result of an operation that can fail for a business (not exceptional) reason.
/// Avoids exceptions for expected validation failures.
/// </summary>
public readonly record struct Result
{
    private Result(bool isSuccess, string? code, string? message)
    {
        IsSuccess = isSuccess;
        ErrorCode = code;
        ErrorMessage = message;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public static Result Success() => new(true, null, null);

    public static Result Failure(string code, string message) => new(false, code, message);
}

/// <summary>
/// Result of an operation that yields a value on success.
/// </summary>
public readonly record struct Result<T>
{
    private Result(bool isSuccess, T? value, string? code, string? message)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorCode = code;
        ErrorMessage = message;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T? Value { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public static Result<T> Success(T value) => new(true, value, null, null);

    public static Result<T> Failure(string code, string message) => new(false, default, code, message);

    public static Result<T> Failure(string code, string message, T fallback) =>
        new(false, fallback, code, message);
}
