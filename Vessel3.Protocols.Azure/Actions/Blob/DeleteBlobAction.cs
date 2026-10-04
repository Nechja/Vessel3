using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Blob;

internal sealed class DeleteBlobAction(
    IObjectStore objects,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.DeleteBlob;

    public Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container) || string.IsNullOrEmpty(target.Blob))
        {
            return Task.FromResult<IResult>(new AzureErrorResult(new InvalidResourceNameError("Container and Blob are required"), errorXml));
        }

        var deleteResult = objects.Delete(target.Container, target.Blob);
        return Task.FromResult<IResult>(
            !deleteResult.TryGetValue(out _, out var err)
                ? new AzureErrorResult(err, errorXml)
                : Results.StatusCode(StatusCodes.Status202Accepted));
    }
}
