namespace VoiceBridge.Core.Results;

public sealed class Result<T>
{
    private Result(T? value, VoiceBridgeError? error, bool isSuccess)
    {
        Value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T? Value { get; }

    public VoiceBridgeError? Error { get; }

    public static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<T>(value, null, isSuccess: true);
    }

    public static Result<T> Failure(VoiceBridgeError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error, isSuccess: false);
    }
}
