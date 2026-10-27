using Vessel3.Primitives;

namespace Vessel3.Client;

internal sealed class AdminClient(VesselHttpTransport transport) : IAdminClient
{
    public async Task<Result<GcReportDto>> RunGc(long minBlobAgeSec = 3600, long minUploadAgeSec = 604800, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Post, $"{VesselRoutes.Gc}?{VesselQueryParams.MinBlobAgeSec}={minBlobAgeSec}&{VesselQueryParams.MinUploadAgeSec}={minUploadAgeSec}");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.GcReportDto, ct);
    }

    public async Task<Result<SweepReportDto>> RunSweep(string? nowOverride = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(nowOverride) ? $"?{VesselQueryParams.Now}={Uri.EscapeDataString(nowOverride)}" : string.Empty;
        using var req = transport.CreateRequest(HttpMethod.Post, $"{VesselRoutes.Sweep}{query}");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.SweepReportDto, ct);
    }

    public async Task<Result<IReadOnlyList<ServerLogEntryDto>>> GetServerLogs(int limit = 100, string? level = null, string? protocol = null, CancellationToken ct = default)
    {
        var query = $"?{VesselQueryParams.Limit}={limit}";
        if (!string.IsNullOrEmpty(level))
        {
            query += $"&{VesselQueryParams.Level}={Uri.EscapeDataString(level)}";
        }

        if (!string.IsNullOrEmpty(protocol))
        {
            query += $"&{VesselQueryParams.Protocol}={Uri.EscapeDataString(protocol)}";
        }

        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Logs}{query}");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.IReadOnlyListServerLogEntryDto, ct);
    }

    public async Task<Result> ClearServerLogs(CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Delete, VesselRoutes.Logs);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<ClusterStatusDto>> GetClusterStatus(CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, VesselRoutes.ClusterStatus);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.ClusterStatusDto, ct);
    }
}
