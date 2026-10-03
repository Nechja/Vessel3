namespace Vessel3.Primitives;

public static class ReservedRoutePrefixes
{
    public const string NativeApi = "/v1";
    public const string OciApi = "/v2";
    public const string WebDav = "/dav";
    public const string WebDavAlt = "/webdav";
    public const string Admin = "/_admin";
    public const string Ui = "/_ui";
    public const string Site = "/_site";

    public static bool Matches(string? path) =>
        !string.IsNullOrEmpty(path) && (
            MatchesPrefix(path, NativeApi)
            || MatchesPrefix(path, OciApi)
            || MatchesPrefix(path, WebDav)
            || MatchesPrefix(path, WebDavAlt)
            || MatchesPrefix(path, Admin)
            || MatchesPrefix(path, Ui)
            || MatchesPrefix(path, Site));

    private static bool MatchesPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
