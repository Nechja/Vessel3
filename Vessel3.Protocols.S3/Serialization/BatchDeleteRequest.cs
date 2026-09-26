namespace Vessel3.Server.S3;

internal sealed record BatchDeleteRequest(IReadOnlyList<BatchDeleteKey> Keys, bool Quiet);
