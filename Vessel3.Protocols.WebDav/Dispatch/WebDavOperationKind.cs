namespace Vessel3.Protocols.WebDav.Dispatch;

public enum WebDavOperationKind
{
    Unknown,
    Options,
    Propfind,
    Get,
    Head,
    Put,
    Delete,
    Mkcol,
    Move,
    Copy,
    Lock,
    Unlock,
    Proppatch
}
