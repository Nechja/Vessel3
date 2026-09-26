namespace Vessel3.Storage;

internal sealed record InProgressUpload(string UploadId, string Bucket, string Key, DateTimeOffset Initiated);
