using Vessel3.Primitives;

namespace Vessel3.Client;

public interface IAdminClient
{
    Task<Result<GcReportDto>> RunGc(long minBlobAgeSec = 3600, long minUploadAgeSec = 604800, CancellationToken ct = default);
    Task<Result<SweepReportDto>> RunSweep(string? nowOverride = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ServerLogEntryDto>>> GetServerLogs(int limit = 100, string? level = null, string? protocol = null, CancellationToken ct = default);
    Task<Result> ClearServerLogs(CancellationToken ct = default);
}
