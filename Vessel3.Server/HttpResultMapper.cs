namespace Vessel3.Server;

internal sealed class HttpResultMapper
{
    public IResult Map(Error error) => Results.StatusCode(error.Status);
}
