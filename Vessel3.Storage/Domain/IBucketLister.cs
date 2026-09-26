namespace Vessel3.Storage;

internal interface IBucketLister
{
    Result<ListPage> List(ListRequest req, string? continuationToken);
}
