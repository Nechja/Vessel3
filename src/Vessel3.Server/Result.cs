namespace Vessel3.Server;

internal abstract record Error(string Code, string Message);

internal sealed record NotFoundError(string Resource) : Error("NotFound", $"{Resource} not found");
internal sealed record InvalidPathError(string Detail) : Error("InvalidPath", Detail);

internal abstract record Result<T>
{
    internal sealed record Success(T Value) : Result<T>;
    internal sealed record Failure(Error Error) : Result<T>;

    public static implicit operator Result<T>(T value) => new Success(value);
    public static implicit operator Result<T>(Error error) => new Failure(error);
}
