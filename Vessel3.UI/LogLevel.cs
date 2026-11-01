namespace Vessel3.UI;

internal static class LogLevel
{
    public static string Label(string level) => level.ToLowerInvariant() switch
    {
        "information" => "info",
        "warning" => "warn",
        "error" or "critical" => "error",
        var other => other
    };

    public static string Badge(string level) => level.ToLowerInvariant() switch
    {
        "information" => "badge-mint",
        "warning" => "badge-warning",
        "error" or "critical" => "badge-danger",
        _ => "badge-neutral"
    };
}
