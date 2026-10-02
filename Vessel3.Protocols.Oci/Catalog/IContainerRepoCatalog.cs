using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci;

internal interface IContainerRepoCatalog : IDisposable, IBlobReferenceSource
{
    string IBlobReferenceSource.ProtocolName => "ContainerRepos";

    Result<ContainerRepo> GetOrCreateRepo(string repoName, string? ownerId = null);
    Result<ContainerRepo?> GetRepo(string repoName);
    Result<IReadOnlyList<string>> ListRepos(int limit = 100, string? last = null);
    Result<IReadOnlyList<string>> ListTags(string repoName, int limit = 100, string? last = null);
    Result<ContainerManifest> GetManifest(string repoName, string reference);
    Result<PutManifestOutcome> PutManifest(string repoName, string reference, string mediaType, byte[] payload, IReadOnlyList<string> layerDigests);
    Result<bool> DeleteManifest(string repoName, string reference);
    Result<bool> DeleteTag(string repoName, string tag);

    Result<ContainerUploadSession> StartUploadSession(string repoName);
    Result<ContainerUploadSession> GetUploadSession(string uploadId);
    Result UpdateUploadSession(string uploadId, long bytesReceived);
    Result CompleteUploadSession(string uploadId);
    Result CancelUploadSession(string uploadId);
    int ReapExpiredUploadSessions(DateTimeOffset cutoff);
}
