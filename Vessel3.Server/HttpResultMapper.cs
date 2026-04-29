namespace Vessel3.Server;

internal interface IHttpResultMapper
{
    IResult Map(Error error);
}

internal sealed class HttpResultMapper : IHttpResultMapper
{
    public IResult Map(Error error) => Results.StatusCode(error.Status);
}
