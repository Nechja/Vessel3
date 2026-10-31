namespace Vessel3.UI;

internal static class Fmt
{
    public static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{n / (1024.0 * 1024):F1} MB",
        _ => $"{n / (1024.0 * 1024 * 1024):F2} GB"
    };
}
