using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles.Permissions;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;

namespace Omnipotent.Tests.KliveAPI;

/// <summary>
/// One path, one handler per HTTP method. Before this, the route table was keyed by path alone and a
/// second registration was dropped without a word: GET /terminal/chart registered first, so the
/// POST the chart actually sends answered 405 MethodNotAllowed.
/// </summary>
public sealed class KliveAPIMethodRoutingTests
{
    private static async Task<ApiService> ApiWithGetAndPostAsync(List<string> calls)
    {
        var api = new ApiService();
        await api.CreateRoute("/thing", req => { calls.Add("GET"); return req.ReturnResponse("{\"via\":\"get\"}", "application/json"); }, HttpMethod.Get, Perms.Public);
        await api.CreateRoute("/thing", req => { calls.Add("POST:" + req.userMessageContent); return req.ReturnResponse("{\"via\":\"post\"}", "application/json"); }, HttpMethod.Post, Perms.Public);
        return api;
    }

    [Fact]
    public async Task Get_And_Post_On_One_Path_Each_Reach_Their_Own_Handler()
    {
        var calls = new List<string>();
        var api = await ApiWithGetAndPostAsync(calls);

        using var post = await SendAsync(api, new HttpRequestMessage(HttpMethod.Post, "/thing") { Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json") });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("post", await post.Content.ReadAsStringAsync());

        using var get = await SendAsync(api, new HttpRequestMessage(HttpMethod.Get, "/thing"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Contains("get", await get.Content.ReadAsStringAsync());

        Assert.Equal(new[] { "POST:{\"a\":1}", "GET" }, calls);
    }

    [Fact]
    public async Task Unregistered_Method_Is_405_Listing_Every_Allowed_Method()
    {
        var calls = new List<string>();
        var api = await ApiWithGetAndPostAsync(calls);

        using var put = await SendAsync(api, new HttpRequestMessage(HttpMethod.Put, "/thing") { Content = new StringContent("{}") });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal("GET, POST", string.Join(", ", put.Content.Headers.Allow.Count > 0 ? put.Content.Headers.Allow : put.Headers.GetValues("Allow")));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Same_Method_Twice_Keeps_The_First_Handler()
    {
        var calls = new List<string>();
        var api = new ApiService();
        await api.CreateRoute("/dup", req => { calls.Add("first"); return req.ReturnResponse("1"); }, HttpMethod.Get, Perms.Public);
        await api.CreateRoute("/dup", req => { calls.Add("second"); return req.ReturnResponse("2"); }, HttpMethod.Get, Perms.Public);

        using var res = await SendAsync(api, new HttpRequestMessage(HttpMethod.Get, "/dup"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(new[] { "first" }, calls);
        Assert.Empty(api.AdditionalMethodRoutes);
        Assert.Contains("GET /dup", Assert.Single(api.RouteCollisions));
    }

    [Fact]
    public async Task Collision_Behind_A_Shared_Wrapper_Is_Reported_But_Registering_Again_Is_Not()
    {
        // Route files wrap every handler in one guard lambda, so the wrapper's method alone cannot tell
        // a service registering its routes again from two different handlers on one path.
        static Func<ApiService.UserRequest, Task> Guard(Func<ApiService.UserRequest, Task> inner) => async req => { try { await inner(req); } catch { } };
        static Func<ApiService.UserRequest, Task> First() => req => req.ReturnResponse("1");
        var api = new ApiService();
        await api.CreateRoute("/w", Guard(First()), HttpMethod.Get, Perms.Public);
        await api.CreateRoute("/w", Guard(First()), HttpMethod.Get, Perms.Public);
        Assert.Empty(api.RouteCollisions);
        await api.CreateRoute("/w", Guard(req => req.ReturnResponse("2")), HttpMethod.Get, Perms.Public);
        Assert.Single(api.RouteCollisions);
    }

    [Fact]
    public async Task Catalog_Lists_Both_Methods_And_Batch_Uses_The_Get()
    {
        var calls = new List<string>();
        var api = new ApiService();
        // POST registered first: batch must still find the GET beside it.
        await api.CreateRoute("/both", req => { calls.Add("POST"); return req.ReturnResponse("{}"); }, HttpMethod.Post, Perms.Public);
        await api.CreateRoute("/both", req => { calls.Add("GET"); return req.ReturnResponse("{\"ok\":true}", "application/json"); }, HttpMethod.Get, Perms.Public);

        var described = api.DescribeRoutes().Where(r => r.Path == "/both").Select(r => r.Method).OrderBy(m => m).ToList();
        Assert.Equal(new[] { "GET", "POST" }, described);

        var result = await (Task<JObject>)typeof(ApiService)
            .GetMethod("ExecuteBatchItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(api, [new ApiService.UserRequest(), "/both"])!;
        Assert.Equal(200, result.Value<int>("status"));
        Assert.Equal(new[] { "GET" }, calls);
    }

    private static async Task<HttpResponseMessage> SendAsync(ApiService api, HttpRequestMessage request)
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
        var process = (Task)typeof(ApiService).GetMethod("ProcessRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(api, [context, 0L])!;
        await process.WaitAsync(TimeSpan.FromSeconds(10));
        return await responseTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
