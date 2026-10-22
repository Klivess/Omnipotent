using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Omnipotent.Profiles.Permissions;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;

namespace Omnipotent.Tests.KliveAPI;

/// <summary>
/// Response paths API telemetry showed going wrong: HEAD answered 500 (the body was written into a HEAD
/// response), a caller hanging up mid-upload was error-logged and counted as a server fault, and pictures
/// went out without validators, so every page view downloaded every one of them again.
/// </summary>
public sealed class KliveAPIResponsePathTests
{
    [Fact]
    public async Task Head_Gets_The_Status_Without_A_Body_Instead_Of_A_500()
    {
        var api = new ApiService();
        await api.CreateRoute("/json", req => req.ReturnResponse("{\"ok\":true}", "application/json"), HttpMethod.Get, Perms.Public);

        using var head = await SendAsync(api, new HttpRequestMessage(HttpMethod.Head, "/json"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var missing = await SendAsync(api, new HttpRequestMessage(HttpMethod.Head, "/nowhere"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task File_Response_Carries_A_Validator_And_An_Unchanged_File_Costs_A_Bodyless_304()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
            var api = new ApiService();
            await api.CreateRoute("/pic", req => req.ReturnFileResponse(path, "image/png", "private, max-age=60"), HttpMethod.Get, Perms.Public);

            using var first = await SendAsync(api, new HttpRequestMessage(HttpMethod.Get, "/pic"));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(5000, (await first.Content.ReadAsByteArrayAsync()).Length);
            Assert.True(first.Headers.CacheControl?.Private);
            Assert.Equal(TimeSpan.FromSeconds(60), first.Headers.CacheControl?.MaxAge);
            string etag = first.Headers.ETag!.ToString();

            var again = new HttpRequestMessage(HttpMethod.Get, "/pic");
            again.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var unchanged = await SendAsync(api, again);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());

            // The picture was made again: the old validator no longer matches.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var stale = new HttpRequestMessage(HttpMethod.Get, "/pic");
            stale.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var remade = await SendAsync(api, stale);
            Assert.Equal(HttpStatusCode.OK, remade.StatusCode);
            Assert.Equal(5000, (await remade.Content.ReadAsByteArrayAsync()).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Validated_Response_Never_Builds_The_Payload_For_A_Matching_Validator()
    {
        var api = new ApiService();
        int loads = 0;
        await api.CreateRoute("/render", req => req.ReturnValidatedBinaryResponse("W/\"v1\"",
            () => { loads++; return Task.FromResult(new byte[] { 1, 2, 3 }); }, "image/png", "private, no-cache"), HttpMethod.Get, Perms.Public);

        var matching = new HttpRequestMessage(HttpMethod.Get, "/render");
        matching.Headers.TryAddWithoutValidation("If-None-Match", "W/\"v1\"");
        using var notModified = await SendAsync(api, matching);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(0, loads);

        using var fresh = await SendAsync(api, new HttpRequestMessage(HttpMethod.Get, "/render"));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await fresh.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Caller_Hanging_Up_Mid_Upload_Is_A_499_And_Never_Reaches_The_Handler()
    {
        var api = new ApiService();
        bool reached = false;
        await api.CreateRoute("/upload", req => { reached = true; return req.ReturnResponse("{}", "application/json"); }, HttpMethod.Post, Perms.Public);

        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        byte[] partial = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n{{\"partial\":");
        await tcp.GetStream().WriteAsync(partial);
        HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        tcp.Client.LingerState = new LingerOption(true, 0); // reset, as a closed tab does
        tcp.Close();

        var process = (Task)typeof(ApiService).GetMethod("ProcessRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(api, [context, 0L])!;
        await process.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reached);
        Assert.Equal(499, context.Response.StatusCode);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<HttpResponseMessage> SendAsync(ApiService api, HttpRequestMessage request)
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Task<HttpResponseMessage> responseTask = client.SendAsync(request);
        HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var process = (Task)typeof(ApiService).GetMethod("ProcessRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(api, [context, 0L])!;
        await process.WaitAsync(TimeSpan.FromSeconds(10));
        return await responseTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
