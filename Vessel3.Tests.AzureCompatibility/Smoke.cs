using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

internal static class Smoke
{
    public static async Task<int> Run(BlobServiceClient serviceClient)
    {
        const string containerName = "vessel3-azure-smoke";
        const string blobName = "smoke.txt";
        var body = Encoding.UTF8.GetBytes("azure smoke test\n");

        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        await Step("CreateContainer", async () =>
        {
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
        });

        await Step("ListContainers", async () =>
        {
            var found = false;
            await foreach (var c in serviceClient.GetBlobContainersAsync())
            {
                if (c.Name == containerName) found = true;
            }
            if (!found) throw new InvalidOperationException($"Container '{containerName}' not found in enumeration");
        });

        await Step("GetContainerProperties", async () =>
        {
            var props = await containerClient.GetPropertiesAsync();
            if (props.Value.LeaseStatus != LeaseStatus.Unlocked)
                throw new InvalidOperationException($"Unexpected lease status: {props.Value.LeaseStatus}");
        });

        await Step("UploadBlob", async () =>
        {
            using var ms = new MemoryStream(body);
            await blobClient.UploadAsync(ms, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "text/plain" }
            });
        });

        await Step("GetBlobProperties", async () =>
        {
            var props = await blobClient.GetPropertiesAsync();
            if (props.Value.ContentLength != body.Length)
                throw new InvalidOperationException($"size {props.Value.ContentLength} != {body.Length}");
        });

        await Step("DownloadBlob", async () =>
        {
            var download = await blobClient.DownloadStreamingAsync();
            using var sink = new MemoryStream();
            await download.Value.Content.CopyToAsync(sink);
            if (!sink.ToArray().AsSpan().SequenceEqual(body))
                throw new InvalidOperationException("body mismatch");
        });

        await Step("DownloadRange", async () =>
        {
            var rangeDownload = await blobClient.DownloadStreamingAsync(new BlobDownloadOptions
            {
                Range = new Azure.HttpRange(0, 5)
            });
            using var sink = new MemoryStream();
            await rangeDownload.Value.Content.CopyToAsync(sink);
            var expected = body.AsSpan(0, 5);
            if (!sink.ToArray().AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("range body mismatch");
        });

        await Step("ListBlobs", async () =>
        {
            var found = false;
            await foreach (var b in containerClient.GetBlobsAsync())
            {
                if (b.Name == blobName) found = true;
            }
            if (!found) throw new InvalidOperationException($"Blob '{blobName}' not found in container enumeration");
        });

        var stagedBlobClient = containerClient.GetBlockBlobClient("staged-blob.txt");
        var chunk1 = Encoding.UTF8.GetBytes("block-one-content\n");
        var chunk2 = Encoding.UTF8.GetBytes("block-two-content\n");
        var id1 = Convert.ToBase64String(Encoding.UTF8.GetBytes("blk001"));
        var id2 = Convert.ToBase64String(Encoding.UTF8.GetBytes("blk002"));

        await Step("StageBlocks", async () =>
        {
            using var s1 = new MemoryStream(chunk1);
            await stagedBlobClient.StageBlockAsync(id1, s1);
            using var s2 = new MemoryStream(chunk2);
            await stagedBlobClient.StageBlockAsync(id2, s2);
        });

        await Step("CommitBlockList", async () =>
        {
            await stagedBlobClient.CommitBlockListAsync(new[] { id1, id2 });
        });

        await Step("DownloadStagedBlob", async () =>
        {
            var dl = await stagedBlobClient.DownloadStreamingAsync();
            using var sink = new MemoryStream();
            await dl.Value.Content.CopyToAsync(sink);
            var expected = new byte[chunk1.Length + chunk2.Length];
            chunk1.CopyTo(expected, 0);
            chunk2.CopyTo(expected, chunk1.Length);
            if (!sink.ToArray().AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("staged blob content mismatch");
        });

        await Step("DeleteBlob", async () =>
        {
            await blobClient.DeleteAsync();
            await stagedBlobClient.DeleteAsync();
        });

        await Step("DeleteContainer", async () =>
        {
            await containerClient.DeleteAsync();
        });

        Console.WriteLine();
        Console.WriteLine("AZURE SMOKE OK");
        return 0;
    }

    private static async Task Step(string name, Func<Task> action)
    {
        Console.Write($"==> {name,-22} ");
        try
        {
            await action();
            Console.WriteLine("ok");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
