namespace RetailCloud.BuildingBlocks.Domain;

/// <summary>A business error with a stable code from the API error catalog (D07 x-error-catalog).</summary>
public sealed record DomainError(string Code, string Message);

/// <summary>Outcome of a domain operation that can fail for business reasons without exceptions.</summary>
public readonly record struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, DomainError? error)
    {
        _value = value;
        Error = error;
    }

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"No value: {Error!.Code}");

    internal static Result<T> Ok(T value) => new(value, null);

    internal static Result<T> Fail(DomainError error) => new(default, error);
}

/// <summary>Factory for <see cref="Result{T}"/>.</summary>
public static class Result
{
    public static Result<T> Success<T>(T value) => Result<T>.Ok(value);

    public static Result<T> Failure<T>(string code, string message) => Result<T>.Fail(new DomainError(code, message));
}

/// <summary>Time abstraction so every time-based rule (QR expiry, reservations, deadlines) is testable (D10 §3).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
