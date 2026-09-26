namespace Vessel3.Storage;

internal sealed record BatchDeleteItem(string Key, string? VersionId, bool BypassGovernance);
