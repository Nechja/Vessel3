using Vessel3.Primitives;

namespace Vessel3.Client;

internal sealed class ContainersClient(VesselHttpTransport transport) : IContainersClient
{
    public async Task<Result<IReadOnlyList<string>>> ListContainerRepos(int limit = 100, string? last = null, CancellationToken ct = default)
    {
        var query = $"?n={limit}" + (last is not null ? $"&last={Uri.EscapeDataString(last)}" : "");
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.OciCatalog}{query}");
        using var res = await transport.SendAsync(req, ct);
        var parsed = await transport.ReadJson(res, VesselJsonContext.Default.ContainerCatalogDto, ct);
        return parsed.TryGetValue(out var dto, out var err)
            ? new Result<IReadOnlyList<string>>.Success(dto.Repositories)
            : new Result<IReadOnlyList<string>>.Failure(err);
    }

    public async Task<Result<IReadOnlyList<string>>> ListContainerTags(string repo, int limit = 100, string? last = null, CancellationToken ct = default)
    {
        var query = $"?n={limit}" + (last is not null ? $"&last={Uri.EscapeDataString(last)}" : "");
        using var req = transport.CreateRequest(HttpMethod.Get, $"v2/{Uri.EscapeDataString(repo)}/tags/list{query}");
        using var res = await transport.SendAsync(req, ct);
        var parsed = await transport.ReadJson(res, VesselJsonContext.Default.ContainerTagsDto, ct);
        return parsed.TryGetValue(out var dto, out var err)
            ? new Result<IReadOnlyList<string>>.Success(dto.Tags)
            : new Result<IReadOnlyList<string>>.Failure(err);
    }

    public async Task<Result> DeleteContainerManifest(string repo, string reference, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Delete, $"v2/{Uri.EscapeDataString(repo)}/manifests/{Uri.EscapeDataString(reference)}");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }
}
