namespace Vessel3.UI;

public static class MimeHelper
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".html", "text/html" },
        { ".htm", "text/html" },
        { ".css", "text/css" },
        { ".js", "text/javascript" },
        { ".mjs", "text/javascript" },
        { ".cjs", "text/javascript" },
        { ".wasm", "application/wasm" },
        { ".json", "application/json" },
        { ".map", "application/json" },
        { ".webmanifest", "application/manifest+json" },
        { ".xml", "application/xml" },
        { ".txt", "text/plain" },
        { ".md", "text/markdown" },
        { ".csv", "text/csv" },
        { ".png", "image/png" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".gif", "image/gif" },
        { ".webp", "image/webp" },
        { ".svg", "image/svg+xml" },
        { ".ico", "image/x-icon" },
        { ".bmp", "image/bmp" },
        { ".avif", "image/avif" },
        { ".tiff", "image/tiff" },
        { ".woff", "font/woff" },
        { ".woff2", "font/woff2" },
        { ".ttf", "font/ttf" },
        { ".otf", "font/otf" },
        { ".eot", "application/vnd.ms-fontobject" },
        { ".dll", "application/octet-stream" },
        { ".dat", "application/octet-stream" },
        { ".blat", "application/octet-stream" },
        { ".bin", "application/octet-stream" },
        { ".pdb", "application/octet-stream" },
        { ".pdf", "application/pdf" },
        { ".zip", "application/zip" },
        { ".gz", "application/gzip" },
        { ".br", "application/x-brotli" },
        { ".mp4", "video/mp4" },
        { ".webm", "video/webm" },
        { ".mp3", "audio/mpeg" },
        { ".wav", "audio/wav" }
    };

    public static string GetMimeType(string fileName, string? browserContentType = null)
    {
        if (!string.IsNullOrEmpty(browserContentType) &&
            !string.Equals(browserContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return browserContentType;
        }

        var ext = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && Extensions.TryGetValue(ext, out var mime))
        {
            return mime;
        }

        return !string.IsNullOrEmpty(browserContentType) ? browserContentType : "application/octet-stream";
    }
}
