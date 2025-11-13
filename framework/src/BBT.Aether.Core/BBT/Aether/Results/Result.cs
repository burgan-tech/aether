namespace BBT.Aether.Results;

/// <summary>
/// Represents the result of an operation without a return value.
/// Provides a throw-free way to handle success and failure cases.
/// </summary>
public readonly record struct Result
{
    /// <summary>
    /// Indicates whether the operation was successful.
    /// </summary>
    public bool IsSuccess { get; }
    
    /// <summary>
    /// Contains error information if the operation failed.
    /// </summary>
    public Error Error { get; }

    private Result(bool ok, Error error)
    {
        IsSuccess = ok;
        Error = error;
    }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static Result Ok() => new(true, Error.None);
    
    /// <summary>
    /// Creates a failed result with the specified error.
    /// </summary>
    public static Result Fail(Error error) => new(false, error);

    /// <summary>
    /// Implicit conversion to bool for simplified success checking.
    /// </summary>
    public static implicit operator bool(Result r) => r.IsSuccess;
}

/// <summary>
/// Represents the result of an operation with a return value of type T.
/// Provides a throw-free way to handle success and failure cases with data.
/// </summary>
/// <typeparam name="T">The type of the value returned on success.</typeparam>
public readonly record struct Result<T>
{
    /// <summary>
    /// Indicates whether the operation was successful.
    /// </summary>
    public bool IsSuccess { get; }
    
    /// <summary>
    /// Contains the value if the operation was successful.
    /// </summary>
    public T? Value { get; }
    
    /// <summary>
    /// Contains error information if the operation failed.
    /// </summary>
    public Error Error { get; }

    private Result(T? value)
    {
        IsSuccess = true;
        Value = value;
        Error = Error.None;
    }

    private Result(Error error)
    {
        IsSuccess = false;
        Value = default;
        Error = error;
    }

    /// <summary>
    /// Creates a successful result with the specified value.
    /// </summary>
    public static Result<T> Ok(T value) => new(value);
    
    /// <summary>
    /// Creates a failed result with the specified error.
    /// </summary>
    public static Result<T> Fail(Error error) => new(error);

    /// <summary>
    /// Deconstructs the result into its components.
    /// </summary>
    public void Deconstruct(out bool ok, out T? value, out Error error)
        => (ok, value, error) = (IsSuccess, Value, Error);
    
    /// <summary>
    /// Implicit conversion to bool for simplified success checking.
    /// </summary>
    public static implicit operator bool(Result<T> r) => r.IsSuccess;
    
    /// <summary>
    /// Converts Result&lt;T&gt; to non-generic Result.
    /// </summary>
    public Result ToResult() => IsSuccess ? Result.Ok() : Result.Fail(Error);
}

