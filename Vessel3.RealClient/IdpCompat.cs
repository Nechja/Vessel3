using System.Diagnostics;
using System.Formats.Tar;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;

internal static class IdpCompat
{
    private const string ClientId = "vessel3";
    private const string ClientSecret = "vessel3-secret";
    private const string StaticAccessKey = "AKIATEST";
    private const string StaticSecretKey = "secretkey1234567890";

    private sealed record Idp(
        string Image, string Issuer, int HostPort, int ContainerPort, string[] Args, string[] Env,
        string ConfigRoot, string ConfigPath, string ConfigBody, int BootSeconds, string RequiredClaim,
        Func<HttpClient, string, Task<string>> Mint);

    public static async Task<int> Run(string? name)
    {
        if (name is null || !Idps.TryGetValue(name, out var idp))
        {
            Console.Error.WriteLine($"usage: idp <{string.Join('|', Idps.Keys)}>");
            return 2;
        }

        var port = int.Parse(Environment.GetEnvironmentVariable("VESSEL3_IDP_PORT") ?? "9400");
        var endpoint = $"http://127.0.0.1:{port}";
        var work = Path.Combine(Path.GetTempPath(), $"vessel3-idp-{name}-{Environment.ProcessId}");
        var container = $"vessel3-idp-{name}-{Environment.ProcessId}";
        Directory.CreateDirectory(work);
        Process? server = null;
        var ok = false;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        try
        {
            await Step($"start {name}", async () =>
            {
                var env = idp.Env.SelectMany(e => new[] { "-e", e });
                Docker(["create", "--name", container, "-p", $"{idp.HostPort}:{idp.ContainerPort}", .. env, idp.Image, .. idp.Args]);
                Docker(["cp", "-", $"{container}:{idp.ConfigRoot}"], stdin: Tar(idp.ConfigPath, idp.ConfigBody));
                Docker(["start", container]);
                await WaitFor(http, idp.Issuer + "/.well-known/openid-configuration", idp.BootSeconds);
            });

            var token = "";
            await Step("mint token", async () =>
            {
                token = await idp.Mint(http, idp.Issuer);
                var header = Base64UrlDecode(token.Split('.')[0]);
                Console.Write($"({header}) ");
            });

            await Step("start vessel3", async () =>
            {
                server = StartServer(ServerBinary(), port, work, idp);
                await WaitFor(http, endpoint + "/", 30, anyStatus: true);
            });

            var sts = new AmazonSecurityTokenServiceClient(new AnonymousAWSCredentials(),
                new AmazonSecurityTokenServiceConfig { ServiceURL = endpoint, AuthenticationRegion = "us-east-1" });
            Credentials? session = null;
            await Step("exchange", async () =>
            {
                var resp = await sts.AssumeRoleWithWebIdentityAsync(new AssumeRoleWithWebIdentityRequest
                {
                    WebIdentityToken = token,
                    RoleArn = "arn:aws:iam::000000000000:role/vessel3",
                    RoleSessionName = "idp-compat",
                    DurationSeconds = 900,
                });
                session = resp.Credentials;
                Console.Write($"(subject {resp.SubjectFromWebIdentityToken}, key {session.AccessKeyId}) ");
            });

            await Step("forged token refused", async () =>
            {
                try
                {
                    await sts.AssumeRoleWithWebIdentityAsync(new AssumeRoleWithWebIdentityRequest
                    {
                        WebIdentityToken = token[..^1] + "x",
                        RoleArn = "arn:aws:iam::000000000000:role/vessel3",
                        RoleSessionName = "forged",
                    });
                    throw new InvalidOperationException("forged token was accepted");
                }
                catch (AmazonSecurityTokenServiceException e) when (e.ErrorCode == "InvalidIdentityToken")
                {
                }
            });

            var s3Config = new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1", UseHttp = true };
            using var s3 = new AmazonS3Client(new SessionAWSCredentials(session!.AccessKeyId, session.SecretAccessKey, session.SessionToken), s3Config);
            var bucket = $"idp-{name}";
            var blob = RandomNumberGenerator.GetBytes(65536);

            await Step("session put/get", async () =>
            {
                await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
                using (var ms = new MemoryStream(blob))
                    await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "blob", InputStream = ms });
                using var got = await s3.GetObjectAsync(bucket, "blob");
                using var sink = new MemoryStream();
                await got.ResponseStream.CopyToAsync(sink);
                if (!sink.ToArray().AsSpan().SequenceEqual(blob)) throw new InvalidOperationException("body mismatch");
            });

            await Step("token-less use refused", async () =>
            {
                using var bare = new AmazonS3Client(new BasicAWSCredentials(session.AccessKeyId, session.SecretAccessKey), s3Config);
                try
                {
                    await bare.ListBucketsAsync();
                    throw new InvalidOperationException("session key accepted without its token");
                }
                catch (AmazonS3Exception e) when (e.ErrorCode == "InvalidToken")
                {
                }
            });

            await Step("static key still works", async () =>
            {
                using var root = new AmazonS3Client(new BasicAWSCredentials(StaticAccessKey, StaticSecretKey), s3Config);
                await root.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket });
            });

            await Step("cleanup", async () =>
            {
                await s3.DeleteObjectAsync(bucket, "blob");
                await s3.DeleteBucketAsync(bucket);
            });

            ok = true;
            Console.WriteLine();
            Console.WriteLine($"IDP COMPAT OK ({name})");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FAILED: {e.Message}");
            return 1;
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(); server.WaitForExit(); }
            if (!ok)
            {
                Console.Error.WriteLine("--- vessel3 log");
                var log = Path.Combine(work, "server.log");
                if (File.Exists(log)) Console.Error.WriteLine(File.ReadAllText(log));
                Console.Error.WriteLine($"--- {name} log");
                Console.Error.WriteLine(string.Join('\n', TryDocker(["logs", "--tail", "20", container]).Split('\n')));
            }
            TryDocker(["rm", "-f", container]);
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static readonly Dictionary<string, Idp> Idps = new(StringComparer.Ordinal)
    {
        ["mynt0"] = new(
            Image: "ghcr.io/nechja/mynt0:latest",
            Issuer: "http://127.0.0.1:5600", HostPort: 5600, ContainerPort: 5000,
            Args: [],
            Env: ["MYNT0_ISSUER=http://127.0.0.1:5600", "MYNT0_SECRET_KEY=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))],
            ConfigRoot: "/etc/mynt0", ConfigPath: "mynt0.json",
            ConfigBody: $$"""
                {
                  "permissions": {
                    "Vessel3.Use": { "description": "Read and write objects in Vessel3", "domain": "vessel3", "delegatable": true }
                  },
                  "clients": [
                    { "id": "{{ClientId}}", "secretSha256": "{{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ClientSecret)))}}", "perms": ["Vessel3.Use"], "tokenFormat": "jwt" }
                  ],
                  "accounts": []
                }
                """,
            BootSeconds: 60,
            RequiredClaim: "perms=Vessel3.Use",
            Mint: (http, issuer) => TokenField(http, issuer + "/token", basic: true, "access_token",
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" })),

        ["dex"] = new(
            Image: "ghcr.io/dexidp/dex:v2.44.0",
            Issuer: "http://127.0.0.1:5556/dex", HostPort: 5556, ContainerPort: 5556,
            Args: ["dex", "serve", "/etc/dex/config.yaml"],
            Env: [],
            ConfigRoot: "/etc/dex", ConfigPath: "config.yaml",
            ConfigBody: $"""
                issuer: http://127.0.0.1:5556/dex
                storage:
                  type: memory
                web:
                  http: 0.0.0.0:5556
                oauth2:
                  passwordConnector: local
                  skipApprovalScreen: true
                staticClients:
                  - id: {ClientId}
                    secret: {ClientSecret}
                    name: Vessel3
                    redirectURIs:
                      - http://127.0.0.1:9400/_ui/
                enablePasswordDB: true
                staticPasswords:
                  - email: kayla@example.com
                    hash: "$2a$10$2b2cU8CPhOTaGrs1HRQuAueS7JTT5ZHsHSzYiFPm1leZck7Mc8T4W"
                    username: kayla
                    userID: 08a8684b-db88-4b73-90a9-3cd1661f5466
                """,
            BootSeconds: 60,
            RequiredClaim: "email=kayla@example.com",
            Mint: (http, issuer) => TokenField(http, issuer + "/token", basic: true, "id_token",
                new Dictionary<string, string>
                {
                    ["grant_type"] = "password", ["username"] = "kayla@example.com", ["password"] = "password",
                    ["scope"] = "openid email profile",
                })),

        ["keycloak"] = new(
            Image: "quay.io/keycloak/keycloak:26.4",
            Issuer: "http://127.0.0.1:8080/realms/vessel3", HostPort: 8080, ContainerPort: 8080,
            Args: ["start-dev", "--import-realm"],
            Env: ["KC_BOOTSTRAP_ADMIN_USERNAME=admin", "KC_BOOTSTRAP_ADMIN_PASSWORD=admin"],
            ConfigRoot: "/opt/keycloak/data", ConfigPath: "import/realm.json",
            ConfigBody: $$"""
                {
                  "realm": "vessel3",
                  "enabled": true,
                  "sslRequired": "none",
                  "clients": [
                    {
                      "clientId": "{{ClientId}}",
                      "enabled": true,
                      "protocol": "openid-connect",
                      "publicClient": false,
                      "secret": "{{ClientSecret}}",
                      "standardFlowEnabled": true,
                      "directAccessGrantsEnabled": true,
                      "redirectUris": ["http://127.0.0.1:9400/_ui/*"]
                    }
                  ],
                  "users": [
                    {
                      "username": "kayla",
                      "enabled": true,
                      "firstName": "Kayla",
                      "lastName": "Graves",
                      "email": "kayla@example.com",
                      "emailVerified": true,
                      "requiredActions": [],
                      "credentials": [{ "type": "password", "value": "password", "temporary": false }]
                    }
                  ]
                }
                """,
            BootSeconds: 180,
            RequiredClaim: "preferred_username=kayla",
            Mint: (http, issuer) => TokenField(http, issuer + "/protocol/openid-connect/token", basic: false, "id_token",
                new Dictionary<string, string>
                {
                    ["grant_type"] = "password", ["client_id"] = ClientId, ["client_secret"] = ClientSecret,
                    ["username"] = "kayla", ["password"] = "password", ["scope"] = "openid email",
                })),
    };

    private static async Task<string> TokenField(HttpClient http, string url, bool basic, string field, Dictionary<string, string> form)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        if (basic)
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"token endpoint {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty(field).GetString()!;
    }

    private static Process StartServer(string binary, int port, string work, Idp idp)
    {
        var psi = new ProcessStartInfo(binary, $"--urls http://127.0.0.1:{port}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["VESSEL3_DATA"] = Path.Combine(work, "data");
        psi.Environment["VESSEL3_ACCESS_KEY"] = StaticAccessKey;
        psi.Environment["VESSEL3_SECRET_KEY"] = StaticSecretKey;
        psi.Environment["VESSEL3_OIDC_ISSUER"] = idp.Issuer;
        psi.Environment["VESSEL3_OIDC_CLIENT_ID"] = ClientId;
        psi.Environment["VESSEL3_OIDC_REQUIRE_CLAIM"] = idp.RequiredClaim;
        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {binary}");
        var log = new StreamWriter(Path.Combine(work, "server.log")) { AutoFlush = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static string ServerBinary()
    {
        if (Environment.GetEnvironmentVariable("VESSEL3_SERVER_BIN") is { Length: > 0 } configured) return configured;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vessel3.slnx")) && !File.Exists(Path.Combine(dir.FullName, "Vessel3.sln"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("set VESSEL3_SERVER_BIN; repo root not found from " + AppContext.BaseDirectory);
        var binary = Path.Combine(dir.FullName, "Vessel3.Server", "bin", "Release", "net10.0", "vessel3");
        if (!File.Exists(binary)) throw new InvalidOperationException($"{binary} missing; run: dotnet build Vessel3.Server -c Release");
        return binary;
    }

    private static async Task WaitFor(HttpClient http, string url, int seconds, bool anyStatus = false)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await http.GetAsync(url);
                if (anyStatus || resp.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new TimeoutException($"{url} not reachable after {seconds}s");
    }

    private static byte[] Tar(string path, string body)
    {
        using var ms = new MemoryStream();
        using (var tar = new TarWriter(ms, leaveOpen: true))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, dir) { Mode = (UnixFileMode)0b111_101_101 });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(body)),
                Mode = (UnixFileMode)0b110_100_100,
            });
        }
        return ms.ToArray();
    }

    private static string Docker(string[] args, byte[]? stdin = null)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin is not null) p.StandardInput.BaseStream.Write(stdin);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"docker {args[0]} failed: {stderr.Trim()}");
        return stdout;
    }

    private static string TryDocker(string[] args)
    {
        try { return Docker(args); }
        catch (InvalidOperationException e) { return e.Message; }
    }

    private static string Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static async Task Step(string name, Func<Task> action)
    {
        Console.Write($"==> {name,-24} ");
        await action();
        Console.WriteLine("ok");
    }
}
