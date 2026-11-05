using Omnipotent.Service_Manager;

namespace Omnipotent.Services.KliveLink;

/// <summary>
/// KliveLink v2 control plane. Kept alongside v1 so existing agents keep working while
/// the native/v2 client rolls out. It owns the SQLite registry, command store, collection
/// store, purge jobs and the v2 WebSocket listener.
/// </summary>
public sealed class KliveLinkV2Service : OmniService
{
    public KliveLinkV2Options Options { get; private set; } = null!;
    public KliveLinkV2Store Store { get; private set; } = null!;
    public KliveLinkV2Server Server { get; private set; } = null!;
    public KliveLinkV2UpdateStore Updates { get; } = new();

    public KliveLinkV2Service()
    {
        name = "KliveLinkV2Service";
        threadAnteriority = ThreadAnteriority.Standard;
    }

    internal KliveLinkV2Service(KliveLinkV2Store store, KliveLinkV2Options options) : this()
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
            Store = new KliveLinkV2Store(Options.DbPath, Options.KeyPath, Options.CollectionDirectory);
            Store.ResetConnections();
            Server = new KliveLinkV2Server(this, Options);
            try { Server.Start(); }
            catch (Exception ex) { _ = ServiceLogError(ex, "KliveLink v2 dedicated listener unavailable; using the KliveAPI 443 route"); }
            var routes = new KliveLinkV2Routes(this);
            _ = Task.Run(async () =>
            {
                try { await routes.CreateRoutes(); }
                catch (Exception ex) { await ServiceLogError(ex, "KliveLink v2 route registration failed"); }
            });
            _ = ServiceLog($"KliveLink v2 started on {Options.ListenPrefix}; db={Options.DbPath}");
            ServiceQuitRequest += () =>
            {
                try { Server?.Stop(); } catch { }
                try { Store?.Dispose(); } catch { }
            };
        }
        catch (Exception ex)
        {
            _ = ServiceLogError(ex, "KliveLink v2 failed to start");
        }
    }

    public Task LogAsync(string message) => ServiceLog(message);
    public Task LogErrorAsync(Exception ex, string message) => ServiceLogError(ex, message);
}
