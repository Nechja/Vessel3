namespace Vessel3.Server.S3;

internal sealed record InProgressUpload(string UploadId, string Bucket, string Key, DateTimeOffset Initiated);
