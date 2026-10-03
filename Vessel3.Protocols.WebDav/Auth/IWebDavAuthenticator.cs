using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Auth;

internal interface IWebDavAuthenticator
{
    Result<CallerIdentity> Authenticate(HttpRequest request);
}
