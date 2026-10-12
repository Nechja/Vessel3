using Vessel3.Primitives;

namespace Vessel3.Client;

public interface IContainersClient
{
    Task<Result<IReadOnlyList<string>>> ListContainerRepos(int limit = 100, string? last = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> ListContainerTags(string repo, int limit = 100, string? last = null, CancellationToken ct = default);
    Task<Result> DeleteContainerManifest(string repo, string reference, CancellationToken ct = default);
}
