using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.S3.Bucket;
using Vessel3.Server.S3.Key;
using Xunit;

namespace Vessel3.Tests;

public class BucketAccessTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly List<IDisposable> disposables = [];

    public BucketAccessTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        foreach (var d in disposables) d.Dispose();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private BucketRegistry Registry()
    {
        var reg = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        disposables.Add(reg);
        return reg;
    }

    [Fact]
    public void Access_Default_Is_Private_And_Persists()
    {
        var reg1 = Registry();
        reg1.Create("b-access");

        var acc1 = reg1.GetAccess("b-access");
        Assert.True(acc1.TryGetValue(out var val1, out _));
        Assert.False(val1.PublicRead);
        Assert.False(val1.ReadOnly);

        reg1.SetAccess("b-access", new BucketAccess(PublicRead: true, ReadOnly: true));

        using var reg2 = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        var acc2 = reg2.GetAccess("b-access");
        Assert.True(acc2.TryGetValue(out var val2, out _));
        Assert.True(val2.PublicRead);
        Assert.True(val2.ReadOnly);
    }

    private sealed class EchoAction(string method, string name) : IS3KeyAction, IS3BucketAction
    {
        public S3KeyRoute Route => new(method, S3KeySubresource.None, S3KeyHeaderFlag.None);
        S3BucketRoute IS3BucketAction.Route => new(method, S3BucketSubresource.None);

        public Task<IResult> Invoke(string bucket, string key, HttpContext ctx) =>
            Task.FromResult<IResult>(Results.Ok(name));

        public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
            Task.FromResult<IResult>(Results.Ok(name));
    }

    [Fact]
    public async Task ReadOnly_Blocks_Mutations_In_Dispatchers()
    {
        var reg = Registry();
        reg.Create("ro-bucket");
        reg.SetAccess("ro-bucket", new BucketAccess(PublicRead: false, ReadOnly: true));

        IS3XmlWriter xml = new S3XmlWriter();
        IHttpResultMapper http = new HttpResultMapper(xml);

        var keyActions = new IS3KeyAction[]
        {
            new EchoAction(HttpMethods.Get, "GetKey"),
            new EchoAction(HttpMethods.Put, "PutKey"),
            new EchoAction(HttpMethods.Delete, "DeleteKey"),
        };
        var keyDispatcher = new S3KeyActionDispatcher(keyActions, reg, http);

        var bucketActions = new IS3BucketAction[]
        {
            new EchoAction(HttpMethods.Get, "GetBucket"),
            new EchoAction(HttpMethods.Delete, "DeleteBucket"),
        };
        var bucketDispatcher = new S3BucketActionDispatcher(bucketActions, reg, http);

        var getRes = await keyDispatcher.Dispatch(HttpMethods.Get, "ro-bucket", "file.txt", new DefaultHttpContext());
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<string>>(getRes);

        var putRes = await keyDispatcher.Dispatch(HttpMethods.Put, "ro-bucket", "file.txt", new DefaultHttpContext());
        var errPut = Assert.IsType<S3ErrorResult>(putRes);
        var ctxPut = new DefaultHttpContext();
        await errPut.ExecuteAsync(ctxPut);
        Assert.Equal(StatusCodes.Status403Forbidden, ctxPut.Response.StatusCode);

        var delRes = await keyDispatcher.Dispatch(HttpMethods.Delete, "ro-bucket", "file.txt", new DefaultHttpContext());
        Assert.IsType<S3ErrorResult>(delRes);

        var getBRes = await bucketDispatcher.Dispatch(HttpMethods.Get, "ro-bucket", new DefaultHttpContext());
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<string>>(getBRes);

        var delBRes = await bucketDispatcher.Dispatch(HttpMethods.Delete, "ro-bucket", new DefaultHttpContext());
        Assert.IsType<S3ErrorResult>(delBRes);
    }

    [Fact]
    public async Task PublicRead_Allows_Anonymous_Bypass_In_Middleware()
    {
        var reg = Registry();
        reg.Create("pub-bucket");
        reg.SetAccess("pub-bucket", new BucketAccess(PublicRead: true, ReadOnly: false));

        reg.Create("priv-bucket");
        reg.SetAccess("priv-bucket", BucketAccess.Private);

        var middleware = new CorsAndAccessMiddleware(reg);

        var pubCtx = new DefaultHttpContext();
        pubCtx.Request.Method = "GET";
        pubCtx.Request.Path = "/pub-bucket/photo.jpg";
        await middleware.InvokeAsync(pubCtx, _ => Task.CompletedTask);
        Assert.True(pubCtx.Items.ContainsKey("AnonymousAllowed"));

        var privCtx = new DefaultHttpContext();
        privCtx.Request.Method = "GET";
        privCtx.Request.Path = "/priv-bucket/photo.jpg";
        await middleware.InvokeAsync(privCtx, _ => Task.CompletedTask);
        Assert.False(privCtx.Items.ContainsKey("AnonymousAllowed"));
    }

    [Fact]
    public async Task Put_And_Get_BucketAcl_Wire_Protocol()
    {
        var reg = Registry();
        reg.Create("acl-bucket");

        IS3XmlWriter xml = new S3XmlWriter();
        IS3XmlReader reader = new S3XmlReader();
        IHttpResultMapper http = new HttpResultMapper(xml);

        var get = new GetBucketAcl(reg, xml, http);
        var put = new PutBucketAcl(reg, reader, http);

        var ctx1 = new DefaultHttpContext();
        var ms1 = new MemoryStream();
        ctx1.Response.Body = ms1;
        await get.Invoke("acl-bucket", ctx1);
        ms1.Position = 0;
        var pub1 = await reader.ReadAccessControlPolicy(ms1, TestContext.Current.CancellationToken);
        Assert.True(pub1.TryGetValue(out var isPub1, out _));
        Assert.False(isPub1);

        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers["x-amz-acl"] = "public-read";
        var putRes = await put.Invoke("acl-bucket", ctx2);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(putRes);

        var acc2 = reg.GetAccess("acl-bucket");
        Assert.True(acc2.TryGetValue(out var val2, out _));
        Assert.True(val2.PublicRead);

        var ctx3 = new DefaultHttpContext();
        var ms3 = new MemoryStream();
        ctx3.Response.Body = ms3;
        await get.Invoke("acl-bucket", ctx3);
        ms3.Position = 0;
        var pub3 = await reader.ReadAccessControlPolicy(ms3, TestContext.Current.CancellationToken);
        Assert.True(pub3.TryGetValue(out var isPub3, out _));
        Assert.True(isPub3);

        var ctx4 = new DefaultHttpContext();
        ctx4.Request.Headers["x-amz-acl"] = "private";
        await put.Invoke("acl-bucket", ctx4);
        var acc4 = reg.GetAccess("acl-bucket");
        Assert.True(acc4.TryGetValue(out var val4, out _));
        Assert.False(val4.PublicRead);
    }

    [Fact]
    public async Task Admin_Get_And_Set_BucketAccess()
    {
        var reg = Registry();
        reg.Create("adm-bucket");

        var services = new ServiceCollection();
        services.AddSingleton<IBucketRegistry>(reg);
        services.AddSingleton<IHttpResultMapper>(new HttpResultMapper(new S3XmlWriter()));
        var sp = services.BuildServiceProvider();

        var getCtx = new DefaultHttpContext { RequestServices = sp };
        var getMs = new MemoryStream();
        getCtx.Response.Body = getMs;
        await AdminEndpoints.GetBucketAccess("adm-bucket", getCtx);

        getMs.Position = 0;
        var json1 = Encoding.UTF8.GetString(getMs.ToArray());
        Assert.Contains("\"publicRead\":false", json1);
        Assert.Contains("\"readOnly\":false", json1);

        var putCtx = new DefaultHttpContext { RequestServices = sp };
        const string updateJson = "{\"publicRead\":true,\"readOnly\":true}";
        putCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(updateJson));
        var putMs = new MemoryStream();
        putCtx.Response.Body = putMs;
        await AdminEndpoints.SetBucketAccess("adm-bucket", putCtx);

        Assert.Equal(200, putCtx.Response.StatusCode);
        var acc = reg.GetAccess("adm-bucket");
        Assert.True(acc.TryGetValue(out var updatedVal, out _));
        Assert.True(updatedVal.PublicRead);
        Assert.True(updatedVal.ReadOnly);
    }
}
