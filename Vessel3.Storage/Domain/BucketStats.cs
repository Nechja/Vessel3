namespace Vessel3.Storage;

internal sealed record BucketStats(string Name, long Versions, long IndexBytes, long WalBytes, long LogBytes);
