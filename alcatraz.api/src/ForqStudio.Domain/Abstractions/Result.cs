using System.Diagnostics.CodeAnalysis;

namespace ForqStudio.Domain.Abstractions;

public class Result
{
    protected internal Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
        {
            throw new InvalidOperationException();
        }

        if (!isSuccess && error == Error.None)
        {
            throw new InvalidOperationException();
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(true, Error.None, value);

    public static Result<TValue> Failure<TValue>(Error error) => new(false, error, default);

    public static Result<TValue> Create<TValue>(TValue? value) =>
        value is not null ? Success(value) : Failure<TValue>(Error.NullValue);
}


public class Result<TValue> : Result
{
    private readonly TValue? _value;

    protected internal Result(bool isSuccess, Error error, TValue value)
        : base(isSuccess, error)
    {
        _value = value;
    }

    [NotNull]
    public TValue Value => IsSuccess
            ? _value!
            : throw new InvalidOperationException("value of failed result cannot be accessed");

    
    
    public static implicit operator Result<TValue>(TValue? value) => Create(value);
}
