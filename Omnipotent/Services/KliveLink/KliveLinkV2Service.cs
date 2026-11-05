using Omnipotent.Service_Manager;

namespace Omnipotent.Services.KliveLink;

/// <summary>
/// KliveLink v2 control plane. Kept alongside v1 so existing agents keep working while
/// the native/v2 client rolls out. It owns the SQLite registry, command store, collection
/// store, purge jobs and the v2 WebSocket listener.
/// </summary>
public sealed class KliveLinkV2Service : OmniService
{
    public static string? LastStartupError { get; private set; }
    public KliveLinkV2Options Options { get; private set; } = null!;
    public KliveLinkV2Store Store { get; private set; } = null!;
    public KliveLinkV2Server Server { get; private set; } = null!;
    public KliveLinkV2UpdateStore Updates { get; } = new();

    public KliveAPI.KliveAPI? InjectedApi { get; }

    public KliveLinkV2Service()
    {
        name = "KliveLinkV2Service";
        threadAnteriority = ThreadAnteriority.Standard;
    }

    public KliveLinkV2Service(KliveAPI.KliveAPI? api) : this()
    {
        InjectedApi = api;
    }

    internal KliveLinkV2Service(KliveLinkV2Store store, KliveLinkV2Options options, KliveAPI.KliveAPI? api = null) : this(api)
    {
        Store = store;
        Options = options;
        Server = new KliveLinkV2Server(this, options);
    }

    protected override void ServiceMain()
    {
        try
        {
            Options = KliveLinkV2Options.Load();
            KliveLinkAssetProvisioner.Ensure(Options);
            Store = new KliveLinkV2Store(Options.DbPath, Options.KeyPath, Options.CollectionDirectory);
            Store.ResetConnections();
            Server = new KliveLinkV2Server(this, Options);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KLIVELINK_V2_LISTEN")))
            {
                try { Server.Start(); }
                catch (Exception ex) { _ = ServiceLogError(ex, "KliveLink v2 dedicated listener unavailable; using the KliveAPI 443 route"); }
            }
            var routes = new KliveLinkV2Routes(this, InjectedApi);
            _ = Task.Run(async () =>
            {
                try
                {
                    await routes.CreateRoutes();
                    LastStartupError = null;
                    await ServiceLog("KliveLink v2 routes registered successfully.");
                }
                catch (Exception ex)
                {
                    LastStartupError = ex.ToString();
                    await ServiceLogError(ex, "KliveLink v2 route registration failed");
                }
            });
            _ = ServiceLog($"KliveLink v2 routes registering on KliveAPI 443; dedicated listener {(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KLIVELINK_V2_LISTEN")) ? "disabled" : Options.ListenPrefix)}; db={Options.DbPath}");
            ServiceQuitRequest += () =>
            {
                try { Server?.Stop(); } catch { }
                try { Store?.Dispose(); } catch { }
            };
        }
        catch (Exception ex)
        {
            LastStartupError = ex.ToString();
            _ = ServiceLogError(ex, "KliveLink v2 failed to start");
        }
    }

    public Task LogAsync(string message) => ServiceLog(message);
    public Task LogErrorAsync(Exception ex, string message) => ServiceLogError(ex, message);
}
