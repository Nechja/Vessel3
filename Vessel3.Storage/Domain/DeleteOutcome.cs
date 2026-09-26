namespace Vessel3.Storage;

internal sealed record DeleteOutcome(string VersionId, bool IsDeleteMarker, bool Found);
