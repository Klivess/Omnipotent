using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI.Caching;
using static Omnipotent.Tests.Profiles.AccessEvaluatorTests;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;
using KMProfile = Omnipotent.Profiles.KMProfileManager.KMProfile;

namespace Omnipotent.Tests.Profiles;

/// <summary>
/// The access contract every client relies on: 401 only means "not signed in (any more)" and makes
/// the website sign out; 403 means "signed in, not allowed" and carries the missing permission.
/// HTTP, /batch and WebSocket upgrades decide identically.
/// </summary>
public sealed class GateContractTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "km-gate-" + Guid.NewGuid().ToString("N"));
    private readonly KMProfileManager manager = new();
    private readonly ApiService api = new();
    private readonly KMProfile guest, owner, disabled, suspended, readOnly;

    public GateContractTests()
    {
        guest = Profile(ProfileRank.Guest, OmniTraderPerms.StatusView.Key);
        owner = Profile(ProfileRank.Klives);
        owner.IsOwner = true;
        disabled = Profile(ProfileRank.Guest, OmniTraderPerms.StatusView.Key);
        disabled.CanLogin = false;
        suspended = Profile(ProfileRank.Guest, OmniTraderPerms.StatusView.Key);
        suspended.SuspendedUntilUtc = DateTime.UtcNow.AddHours(1);
        suspended.SuspensionReason = "testing";
        readOnly = Profile(ProfileRank.Guest, OmniTraderPerms.StatusView.Key, OmniTraderPerms.BacktestsRun.Key);
        readOnly.ReadOnly = true;

        manager.InitializeForTests(new[] { guest, owner, disabled, suspended, readOnly }, dir);
        typeof(ApiService).GetField("profileManager", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(api, manager);

        Task Ok(ApiService.UserRequest r) => r.ReturnResponse("{\"ok\":true}");
        api.CreateRoute("/t/status", Ok, HttpMethod.Get, OmniTraderPerms.StatusView).Wait();
        api.CreateRoute("/t/orders", Ok, HttpMethod.Get, OmniTraderPerms.OrdersPlace).Wait();
        api.CreateRoute("/t/me", Ok, HttpMethod.Get, Perms.SignedIn).Wait();
        api.CreateRoute("/t/public", Ok, HttpMethod.Get, Perms.Public).Wait();
        api.CreateRoute("/t/backtest", Ok, HttpMethod.Post, OmniTraderPerms.BacktestsRun).Wait();
        api.CreateWebSocketRoute("/t/ws", async (_, socket, _, _) =>
        {
            // CloseOutput does not wait for the client's half of the close handshake.
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }, OmniTraderPerms.StatusView).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    private string Token(KMProfile p) => manager.Sessions.Issue(p.UserID, "127.0.0.1", "test").Token;

    private static string? Header(HttpResponseMessage r, string name)
        => r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private async Task<(HttpStatusCode Status, JObject? Body, string? Code)> Call(string path, string? credential, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (credential != null) request.Headers.TryAddWithoutValidation("Authorization", credential);
        if ((method ?? HttpMethod.Get) == HttpMethod.Post) request.Content = new StringContent("{}");
        using var response = await ServeAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        JObject? body = null;
        try { body = JObject.Parse(text); } catch { }
        return (response.StatusCode, body, Header(response, "RequestDeniedCode"));
    }

    [Fact]
    public async Task Allowed_RunsTheHandler()
    {
        var (status, body, _) = await Call("/t/status", "Bearer " + Token(guest));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!.Value<bool>("ok"));
    }

    [Fact]
    public async Task MissingPermission_Is403_AndNamesThePermission()
    {
        var (status, body, code) = await Call("/t/orders", "Bearer " + Token(guest));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("2", code);
        Assert.Equal("AccessDenied", body!.Value<string>("error"));
        Assert.Equal("MissingPermission", body.Value<string>("reason"));
        Assert.Equal(OmniTraderPerms.OrdersPlace.Key, body["permission"]!.Value<string>("key"));
        Assert.Equal(OmniTraderPerms.OrdersPlace.Title, body["permission"]!.Value<string>("title"));
    }

    [Fact]
    public async Task NoCredential_Is401()
    {
        var (status, body, code) = await Call("/t/status", null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("0", code);
        Assert.Equal("Unauthorized", body!.Value<string>("error"));
    }

    [Fact]
    public async Task InvalidCredential_Is401()
    {
        var (status, _, code) = await Call("/t/status", "kms_not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("1", code);
    }

    [Fact]
    public async Task RevokedSession_Is401_WithItsReason()
    {
        var (token, session) = manager.Sessions.Issue(guest.UserID, null, null);
        manager.Sessions.Revoke(session.SessionId, owner.UserID, "test");
        var (status, body, code) = await Call("/t/status", token);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("5", code);
        Assert.Equal("SessionRevoked", body!.Value<string>("reason"));
    }

    [Fact]
    public async Task DisabledProfile_Is401_SoTheSiteSignsOut()
    {
        var (status, _, code) = await Call("/t/me", Token(disabled));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("4", code);
    }

    [Fact]
    public async Task Suspended_Is403_ButSignedInRoutesStillAnswer()
    {
        string token = Token(suspended);
        var (status, body, code) = await Call("/t/status", token);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("7", code);
        Assert.Equal("testing", body!.Value<string>("suspensionReason"));
        Assert.Equal(HttpStatusCode.OK, (await Call("/t/me", token)).Status);
    }

    [Fact]
    public async Task ReadOnly_BlocksActions_ButNotReads()
    {
        string token = Token(readOnly);
        Assert.Equal(HttpStatusCode.OK, (await Call("/t/status", token)).Status);
        var (status, _, code) = await Call("/t/backtest", token, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("8", code);
    }

    [Fact]
    public async Task Owner_PassesEverything()
    {
        Assert.Equal(HttpStatusCode.OK, (await Call("/t/orders", Token(owner))).Status);
    }

    [Fact]
    public async Task Public_NeedsNothing_AndIgnoresABadCredential()
    {
        Assert.Equal(HttpStatusCode.OK, (await Call("/t/public", null)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call("/t/public", "garbage")).Status);
    }

    [Fact]
    public async Task Batch_DecidesExactlyLikeDirectRequests()
    {
        var auth = manager.Authenticate(Token(guest));
        var batchReq = new ApiService.UserRequest { user = auth.Profile, session = auth.Session, ParentService = api };
        var exec = typeof(ApiService).GetMethod("ExecuteBatchItem", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var denied = await (Task<JObject>)exec.Invoke(api, [batchReq, "/t/orders"])!;
        Assert.Equal(403, denied.Value<int>("status"));
        Assert.Equal(OmniTraderPerms.OrdersPlace.Key, denied["body"]!["permission"]!.Value<string>("key"));

        var allowed = await (Task<JObject>)exec.Invoke(api, [batchReq, "/t/status"])!;
        Assert.Equal(200, allowed.Value<int>("status"));
    }

    [Fact]
    public async Task WebSocket_UsesTheSameGate_IncludingDisabledProfiles()
    {
        await ConnectWs("/t/ws?authorization=" + Uri.EscapeDataString(Token(guest)), expectSuccess: true);
        await ConnectWs("/t/ws?authorization=" + Uri.EscapeDataString(Token(disabled)), expectSuccess: false);
        await ConnectWs("/t/ws", expectSuccess: false);
    }

    [Fact]
    public async Task SessionWatch_BeforeProfilesLoad_SaysTryAgain_NotSignedOut()
    {
        // A restart: the listener is up but profiles aren't loaded. Every open tab reconnects its
        // SessionWatch now; telling them "ProfileNotFound" would sign the whole site out.
        var starting = new KMProfileManager();
        var handler = typeof(KMProfileManager).GetMethod("HandleSessionWatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await api.CreateWebSocketRoute("/t/watch", (ctx, socket, query, _) => (Task)handler.Invoke(starting, [ctx, socket, query])!, Perms.Public);

        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var client = new ClientWebSocket();
        var connect = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/t/watch?authorization=" + Uri.EscapeDataString(Token(guest))), CancellationToken.None);
        var serve = Invoke(await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await connect.WaitAsync(TimeSpan.FromSeconds(10));

        var buffer = new byte[1024];
        var first = await client.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebSocketMessageType.Close, first.MessageType); // no "session-state" message at all
        Assert.Equal(KMProfileManager.TryAgainLater, client.CloseStatus);
        await serve.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void InHandlerChecks_RegisterTheProfilesAccessAsACacheDependency()
    {
        var scope = CacheDeps.OpenScope();
        var req = new ApiService.UserRequest { user = guest };
        Assert.True(req.Can(OmniTraderPerms.StatusView));
        CacheDeps.Seal(scope);
        Assert.Contains(KMProfileManager.AccessDependencyKey(guest.UserID), scope.SnapshotReads().Keys);
    }

    // ───────────── harness ─────────────

    private async Task ConnectWs(string pathAndQuery, bool expectSuccess)
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var client = new ClientWebSocket();
        var connect = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{pathAndQuery}"), CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var serve = Invoke(context);
        if (expectSuccess)
        {
            await connect.WaitAsync(TimeSpan.FromSeconds(10)); // throws if the upgrade was refused
            Assert.NotEqual(WebSocketState.None, client.State);
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => connect.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await serve.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private Task Invoke(HttpListenerContext context)
        => (Task)typeof(ApiService).GetMethod("ProcessRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(api, [context, 0L])!;

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<HttpResponseMessage> ServeAsync(HttpRequestMessage request)
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Task<HttpResponseMessage> responseTask = client.SendAsync(request);
        HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Invoke(context).WaitAsync(TimeSpan.FromSeconds(10));
        return await responseTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
