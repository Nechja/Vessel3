using Vessel3.Server.S3.Bucket;
using Vessel3.Server.S3.Key;

namespace Vessel3.Server.S3;

internal static class S3ServiceCollectionExtensions
{
    public static IServiceCollection AddS3BucketActions(this IServiceCollection services)
    {
        services.AddSingleton<IS3BucketAction, GetBucketLocation>();
        services.AddSingleton<IS3BucketAction, ListMultipartUploads>();
        services.AddSingleton<IS3BucketAction, GetBucketVersioning>();
        services.AddSingleton<IS3BucketAction, PutBucketVersioning>();
        services.AddSingleton<IS3BucketAction, GetObjectLockConfiguration>();
        services.AddSingleton<IS3BucketAction, PutObjectLockConfiguration>();
        services.AddSingleton<IS3BucketAction, GetBucketLifecycleConfiguration>();
        services.AddSingleton<IS3BucketAction, PutBucketLifecycleConfiguration>();
        services.AddSingleton<IS3BucketAction, DeleteBucketLifecycle>();
        services.AddSingleton<IS3BucketAction, ListObjectVersions>();
        services.AddSingleton<IS3BucketAction, ListObjects>();
        services.AddSingleton<IS3BucketAction, CreateBucket>();
        services.AddSingleton<IS3BucketAction, DeleteBucket>();
        services.AddSingleton<IS3BucketAction, HeadBucket>();
        services.AddSingleton<IS3BucketAction, DeleteObjects>();
        services.AddSingleton<IS3BucketActionDispatcher, S3BucketActionDispatcher>();
        return services;
    }

    public static IServiceCollection AddS3KeyActions(this IServiceCollection services)
    {
        services.AddSingleton<IS3KeyAction, InitiateMultipartUpload>();
        services.AddSingleton<IS3KeyAction, CompleteMultipartUpload>();
        services.AddSingleton<IS3KeyAction, PutObjectTagging>();
        services.AddSingleton<IS3KeyAction, PutObjectRetention>();
        services.AddSingleton<IS3KeyAction, PutObjectLegalHold>();
        services.AddSingleton<IS3KeyAction, UploadPart>();
        services.AddSingleton<IS3KeyAction, UploadPartCopy>();
        services.AddSingleton<IS3KeyAction, PutObject>();
        services.AddSingleton<IS3KeyAction, CopyObject>();
        services.AddSingleton<IS3KeyAction, GetObjectAttributes>();
        services.AddSingleton<IS3KeyAction, GetObjectTagging>();
        services.AddSingleton<IS3KeyAction, GetObjectRetention>();
        services.AddSingleton<IS3KeyAction, GetObjectLegalHold>();
        services.AddSingleton<IS3KeyAction, ListParts>();
        services.AddSingleton<IS3KeyAction, GetObject>();
        services.AddSingleton<IS3KeyAction, HeadObject>();
        services.AddSingleton<IS3KeyAction, DeleteObjectTagging>();
        services.AddSingleton<IS3KeyAction, AbortMultipartUpload>();
        services.AddSingleton<IS3KeyAction, DeleteObject>();
        services.AddSingleton<IS3KeyActionDispatcher, S3KeyActionDispatcher>();
        return services;
    }
}
