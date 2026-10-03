using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class PropfindAction(
    IBucketRegistry registry,
    IBucketLister lister,
    IObjectStore objects,
    IWebDavXmlWriter xml) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Propfind;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        var basePrefix = DetermineBasePrefix(ctx.Request.Path.Value);
        var caller = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci
            ? ci
            : CallerIdentity.System;

        var entriesResult = target switch
        {
            { Bucket: null } => BuildServiceRootEntries(basePrefix, target.Depth, caller),
            { Path: null } => BuildBucketRootEntries(basePrefix, target.Bucket, target.Depth),
            _ => BuildSubpathEntries(basePrefix, target.Bucket, target.Path, target.Depth)
        };

        if (!entriesResult.TryGetValue(out var entries, out var error))
        {
            return new WebDavErrorResult(error);
        }

        ctx.Response.StatusCode = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        ctx.Response.Headers["DAV"] = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";

        await xml.WriteMultistatus(ctx.Response.Body, entries, ctx.RequestAborted);
        return Results.Empty;
    }

    private static string DetermineBasePrefix(string? path) =>
        path?.StartsWith("/webdav", StringComparison.OrdinalIgnoreCase) is true
            ? "/webdav"
            : "/dav";

    private Result<List<WebDavResourceEntry>> BuildServiceRootEntries(string basePrefix, int depth, CallerIdentity caller)
    {
        var entries = new List<WebDavResourceEntry>
        {
            new(
                Href: $"{basePrefix}/",
                DisplayName: string.Empty,
                IsCollection: true,
                ContentLength: 0,
                ContentType: null,
                ETag: null,
                LastModified: DateTimeOffset.UtcNow,
                CreationDate: DateTimeOffset.UtcNow)
        };

        if (depth <= 0)
        {
            return entries;
        }

        foreach (var b in registry.List(caller))
        {
            entries.Add(new WebDavResourceEntry(
                Href: $"{basePrefix}/{Uri.EscapeDataString(b.Name)}/",
                DisplayName: b.Name,
                IsCollection: true,
                ContentLength: 0,
                ContentType: null,
                ETag: null,
                LastModified: b.CreatedAt,
                CreationDate: b.CreatedAt));
        }

        return entries;
    }

    private Result<List<WebDavResourceEntry>> BuildBucketRootEntries(string basePrefix, string bucket, int depth)
    {
        if (!registry.Exists(bucket).TryGetValue(out var exists, out var err) || !exists)
        {
            return err ?? new NoSuchBucketError(bucket);
        }

        var bucketHref = $"{basePrefix}/{Uri.EscapeDataString(bucket)}/";
        var entries = new List<WebDavResourceEntry>
        {
            new(
                Href: bucketHref,
                DisplayName: bucket,
                IsCollection: true,
                ContentLength: 0,
                ContentType: null,
                ETag: null,
                LastModified: DateTimeOffset.UtcNow,
                CreationDate: DateTimeOffset.UtcNow)
        };

        if (depth <= 0)
        {
            return entries;
        }

        var listRes = lister.List(new ListRequest(bucket, null, "/", null, 5000), null);
        if (!listRes.TryGetValue(out var page, out var listErr))
        {
            return listErr;
        }

        AppendPageEntries(page.Entries, bucketHref, entries);
        return entries;
    }

    private Result<List<WebDavResourceEntry>> BuildSubpathEntries(string basePrefix, string bucket, string path, int depth)
    {
        if (!registry.Exists(bucket).TryGetValue(out var exists, out var err) || !exists)
        {
            return err ?? new NoSuchBucketError(bucket);
        }

        var bucketHref = $"{basePrefix}/{Uri.EscapeDataString(bucket)}/";
        var cleanPath = path.TrimStart('/');

        var statRes = objects.Stat(bucket, cleanPath);
        return statRes.TryGetValue(out var stat, out _)
            ? BuildExplicitItemEntries(bucket, cleanPath, bucketHref, stat, depth)
            : BuildVirtualDirectoryEntries(bucket, cleanPath, bucketHref, depth);
    }

    private Result<List<WebDavResourceEntry>> BuildExplicitItemEntries(
        string bucket,
        string cleanPath,
        string bucketHref,
        ObjectStat stat,
        int depth)
    {
        var isCollection = cleanPath.EndsWith('/');
        var href = $"{bucketHref}{EncodePath(cleanPath)}";
        var displayName = cleanPath.TrimEnd('/').Split('/').Last();

        if (!isCollection)
        {
            return new List<WebDavResourceEntry>
            {
                new(
                    Href: href,
                    DisplayName: displayName,
                    IsCollection: false,
                    ContentLength: stat.Size,
                    ContentType: stat.ContentType,
                    ETag: stat.Etag,
                    LastModified: stat.LastModified,
                    CreationDate: stat.LastModified)
            };
        }

        var entries = new List<WebDavResourceEntry>
        {
            new(
                Href: href,
                DisplayName: displayName,
                IsCollection: true,
                ContentLength: 0,
                ContentType: null,
                ETag: null,
                LastModified: stat.LastModified,
                CreationDate: stat.LastModified)
        };

        if (depth > 0)
        {
            AppendChildren(bucket, cleanPath, bucketHref, entries);
        }

        return entries;
    }

    private Result<List<WebDavResourceEntry>> BuildVirtualDirectoryEntries(
        string bucket,
        string cleanPath,
        string bucketHref,
        int depth)
    {
        var dirPrefix = cleanPath.TrimEnd('/') + "/";
        var listRes = lister.List(new ListRequest(bucket, dirPrefix, "/", null, 5000), null);
        if (!listRes.TryGetValue(out var page, out _) || page.Entries.Count == 0)
        {
            return new NoSuchKeyError(cleanPath);
        }

        var href = $"{bucketHref}{EncodePath(dirPrefix)}";
        var displayName = cleanPath.TrimEnd('/').Split('/').Last();
        var entries = new List<WebDavResourceEntry>
        {
            new(
                Href: href,
                DisplayName: displayName,
                IsCollection: true,
                ContentLength: 0,
                ContentType: null,
                ETag: null,
                LastModified: DateTimeOffset.UtcNow,
                CreationDate: DateTimeOffset.UtcNow)
        };

        if (depth > 0)
        {
            AppendChildren(bucket, dirPrefix, bucketHref, entries);
        }

        return entries;
    }

    private void AppendChildren(string bucket, string prefix, string bucketHref, List<WebDavResourceEntry> entries)
    {
        if (!lister.List(new ListRequest(bucket, prefix, "/", null, 5000), null).TryGetValue(out var page, out _))
        {
            return;
        }

        foreach (var entry in page.Entries)
        {
            if (string.Equals(entry.Key, prefix, StringComparison.Ordinal))
            {
                continue;
            }

            entries.Add(CreateEntryFromList(entry, bucketHref));
        }
    }

    private static void AppendPageEntries(
        IEnumerable<ListEntry> pageEntries,
        string bucketHref,
        List<WebDavResourceEntry> entries)
    {
        foreach (var entry in pageEntries)
        {
            entries.Add(CreateEntryFromList(entry, bucketHref));
        }
    }

    private static WebDavResourceEntry CreateEntryFromList(ListEntry entry, string bucketHref) => entry switch
    {
        ListEntry.CommonPrefix cp => CreateFolderEntry(cp.Key.TrimEnd('/'), bucketHref, DateTimeOffset.UtcNow),
        ListEntry.Contents c when c.Key.EndsWith('/') => CreateFolderEntry(c.Key.TrimEnd('/'), bucketHref, c.LastModified),
        ListEntry.Contents c => new WebDavResourceEntry(
            Href: $"{bucketHref}{EncodePath(c.Key)}",
            DisplayName: c.Key.Split('/').Last(),
            IsCollection: false,
            ContentLength: c.Size,
            ContentType: "application/octet-stream",
            ETag: c.Etag,
            LastModified: c.LastModified,
            CreationDate: c.LastModified),
        _ => CreateFolderEntry(entry.Key.TrimEnd('/'), bucketHref, DateTimeOffset.UtcNow)
    };

    private static WebDavResourceEntry CreateFolderEntry(string folderName, string bucketHref, DateTimeOffset timestamp) =>
        new(
            Href: $"{bucketHref}{EncodePath(folderName)}/",
            DisplayName: folderName.Split('/').Last(),
            IsCollection: true,
            ContentLength: 0,
            ContentType: null,
            ETag: null,
            LastModified: timestamp,
            CreationDate: timestamp);

    private static string EncodePath(string path)
    {
        var trailing = path.EndsWith('/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var encoded = string.Join('/', segments.Select(Uri.EscapeDataString));
        return trailing ? encoded + "/" : encoded;
    }
}
