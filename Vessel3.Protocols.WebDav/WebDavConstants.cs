namespace Vessel3.Protocols.WebDav;

internal static class WebDavHeaders
{
    public const string Dav = "DAV";
    public const string MsAuthorVia = "MS-Author-Via";
    public const string Destination = "Destination";
    public const string Overwrite = "Overwrite";
    public const string Depth = "Depth";
    public const string LockToken = "Lock-Token";
    public const string Allow = "Allow";
    public const string Translate = "Translate";
    public const string AcceptRanges = "Accept-Ranges";
    public const string WwwAuthenticate = "WWW-Authenticate";
    public const string DavComplianceLevel = "1, 2";
    public const string DavAuthorValue = "DAV";
    public const string BasicRealm = "Basic realm=\"Vessel3 WebDAV\"";
    public const string BasicScheme = "Basic ";
    public const string AllowedMethods = "OPTIONS, GET, HEAD, POST, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
    public const string BytesUnit = "bytes";
}

internal static class WebDavMediaTypes
{
    public const string XmlUtf8 = "application/xml; charset=utf-8";
    public const string OctetStream = "application/octet-stream";
    public const string Directory = "application/x-directory";
}

internal static class WebDavRoutes
{
    public const string DavPrefix = "/dav";
    public const string WebDavPrefix = "/webdav";
    public const string DavPrefixWithSlash = "/dav/";
    public const string WebDavPrefixWithSlash = "/webdav/";
    public const string Root = "/";
}

internal static class WebDavXmlElements
{
    public const string Multistatus = "multistatus";
    public const string Response = "response";
    public const string Href = "href";
    public const string Propstat = "propstat";
    public const string Prop = "prop";
    public const string Status = "status";
    public const string ResourceType = "resourcetype";
    public const string Collection = "collection";
    public const string GetContentLength = "getcontentlength";
    public const string GetContentType = "getcontenttype";
    public const string GetETag = "getetag";
    public const string GetLastModified = "getlastmodified";
    public const string CreationDate = "creationdate";
    public const string DisplayName = "displayname";
    public const string LockDiscovery = "lockdiscovery";
    public const string ActiveLock = "activelock";
    public const string LockType = "locktype";
    public const string Write = "write";
    public const string LockScope = "lockscope";
    public const string Exclusive = "exclusive";
    public const string Depth = "depth";
    public const string Owner = "owner";
    public const string Timeout = "timeout";
    public const string LockToken = "locktoken";
    public const string LockRoot = "lockroot";
    public const string Error = "error";
    public const string Message = "message";
}

internal static class WebDavHttpStatusStrings
{
    public const string Http200 = "HTTP/1.1 200 OK";
    public const string Http404 = "HTTP/1.1 404 Not Found";
}

internal static class WebDavItems
{
    public const string CallerIdentity = "CallerIdentity";
}
