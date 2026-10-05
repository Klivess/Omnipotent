using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniDefence;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;

namespace Omnipotent.Tests.KliveAPI;

public sealed class OmniSettingsRequestSecurityTests
{
    [Fact]
    public void Audit_RemovesPayloadAndUntrustedMetadataButPreservesAccessOutcome()
    {
        var row = new RequestRow
        {
            Route = "/OmniGlobalSettings/Set",
            Query = "?value=secret",
            BodyText = "{\"value\":\"secret\"}",
            BodyHash = "secret-checking-oracle",
            BodyLength = 18,
            BodyTruncated = true,
            HeadersJson = "{\"X-Secret\":\"secret\"}",
            UserAgent = "secret",
            ClientPage = "https://example.com/?secret",
            Ip = "127.0.0.1",
            ProfileId = "admin",
            StatusCode = 403,
            DenyReason = "HTTPSRequired"
        };

        ApiService.RedactSensitiveSettingsRequestAudit(row);

        Assert.Null(row.Query);
        Assert.Null(row.BodyText);
        Assert.Null(row.BodyHash);
        Assert.Equal(0, row.BodyLength);
        Assert.False(row.BodyTruncated);
        Assert.Null(row.HeadersJson);
        Assert.Null(row.UserAgent);
        Assert.Null(row.ClientPage);
        Assert.Equal("admin", row.ProfileId);
        Assert.Equal("127.0.0.1", row.Ip);
        Assert.Equal(403, row.StatusCode);
        Assert.Equal("HTTPSRequired", row.DenyReason);
    }

    [Theory]
    [InlineData("private, no-store")]
    [InlineData("NO-STORE, max-age=60")]
    public void Cache_RejectsNoStoreAcrossDirectAndBatchWrites(string cacheControl)
    {
        var cache = new ResponseCache();
        var scope = CacheDeps.OpenScope();
        CacheDeps.NoteRead("security-test:" + Guid.NewGuid());
        CacheDeps.Seal(scope);
        var headers = new NameValueCollection { ["Cache-Control"] = cacheControl };
        byte[] body = "secret"u8.ToArray();
        var recording = new ResponseRecording();
        recording.Record(200, "application/json", headers, body, false);

        Assert.False(cache.TryStoreFromRecording("direct", recording, scope));
        Assert.False(cache.TryStoreFromParts("batch", 200, "application/json", headers, body, false, scope));
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public async Task SettingsResponse_IsNoStoreWithoutETagOrCompression()
    {
        string body = Newtonsoft.Json.JsonConvert.SerializeObject(new { Value = new string('s', 4096) });
        var request = new HttpRequestMessage(HttpMethod.Get, "/OmniGlobalSettings/List");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");
        request.Headers.TryAddWithoutValidation("If-None-Match", HttpResponseHelpers.ComputeWeakETag(Encoding.UTF8.GetBytes(body)));

        using HttpResponseMessage response = await ServeAsync(request, async context =>
        {
            var req = new ApiService.UserRequest
            {
                route = "/OmniGlobalSettings/List",
                req = context.Request,
                context = context,
                ParentService = new ApiService()
            };
            await req.ReturnResponse(body);
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task InsecureSettingsRequest_IsRejectedBeforeDispatchDespiteForwardedHttps()
    {
        var api = new ApiService();
        bool dispatched = false;
        api.ControllerLookup["/OmniGlobalSettings/Set"] = new ApiService.RouteInfo
        {
            normalizedMethod = "POST",
            action = _ => { dispatched = true; return Task.CompletedTask; }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/OmniGlobalSettings/Set")
        {
            Content = new StringContent("{\"value\":\"secret\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "secret-profile-password");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        using HttpResponseMessage response = await ServeAsync(request, context =>
            (Task)typeof(ApiService).GetMethod("ProcessRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(api, [context, 0L])!);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("HTTPSRequired", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(dispatched);
    }

    [Theory]
    [InlineData("/OmniGlobalSettings/List?revealSensitive=true")]
    [InlineData("omniGLOBALsettings/get/?name=secret")]
    public async Task Batch_CannotBypassSettingsTransportAndConfidentialityPolicy(string path)
    {
        var api = new ApiService();
        var result = await (Task<JObject>)typeof(ApiService)
            .GetMethod("ExecuteBatchItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(api, [new ApiService.UserRequest(), path])!;

        Assert.Equal(400, result.Value<int>("status"));
        Assert.Contains("HTTPS", result.ToString());
    }

    private static async Task<HttpResponseMessage> ServeAsync(HttpRequestMessage request, Func<HttpListenerContext, Task> handler)
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Task<HttpResponseMessage> responseTask = client.SendAsync(request);
        HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await handler(context).WaitAsync(TimeSpan.FromSeconds(10));
        return await responseTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
