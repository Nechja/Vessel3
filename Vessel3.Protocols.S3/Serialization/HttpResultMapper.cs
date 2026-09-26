using Vessel3.Server.S3;

namespace Vessel3.Server;

internal interface IHttpResultMapper
{
    IResult Map(Error error);
}

internal sealed class HttpResultMapper(IS3ErrorXmlWriter xml) : IHttpResultMapper
{
    public IResult Map(Error error) => new S3ErrorResult(error, xml);
}
