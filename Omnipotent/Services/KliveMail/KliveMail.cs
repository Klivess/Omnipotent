using System.Security.Cryptography.X509Certificates;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveMail.Persistence;
using Omnipotent.Services.KliveMail.Smtp;
using SmtpServer;
using KliveApiService = Omnipotent.Services.KliveAPI.KliveAPI;
using PortForwardManagerService = Omnipotent.Services.PortForwardManager.PortForwardManager;

namespace Omnipotent.Services.KliveMail
{
    // Receive-only, self-hosted email for @klive.dev. Embeds an SMTP server (SmtpServer lib) on
    // port 25, accepts any address (catch-all), parses with MimeKit and stores to SQLite. A web
    // client reads it via the /klivemail/* API routes.
    public sealed class KliveMail : OmniService
    {
        public const int SmtpPort = 25;

        public KliveMailDb Db { get; private set; } = null!;
        public KliveMailRepository Repo { get; private set; } = null!;

        private KliveMailRoutes routes = null!;
        private SmtpServer.SmtpServer? smtpServer;

        public KliveMail()
        {
            name = "KliveMail";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        public string GetDbPath() => Db?.DbPath ?? "(uninitialised)";

        protected override async void ServiceMain()
        {
            try
            {
                Db = new KliveMailDb();
                await Db.InitialiseAsync();
                Repo = new KliveMailRepository(Db);

                routes = new KliveMailRoutes(this);
                await routes.RegisterRoutes();

                await TryEnsurePortForwardAsync();

                // The SMTP listener runs for the lifetime of the service on its own task.
                _ = Task.Run(() => RunSmtpServerAsync(cancellationToken.Token));

                await ServiceLog($"KliveMail started. DB={Db.DbPath}. SMTP on :{SmtpPort} (catch-all @{KliveMailRepository.MailDomain}).");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveMail startup failed");
            }
        }

        private async Task TryEnsurePortForwardAsync()
        {
            try
            {
                var services = await GetServicesByType<PortForwardManagerService>();
                if (services != null && services.Length > 0)
                {
                    bool added = await ((PortForwardManagerService)services[0]).EnsurePortForwarded(SmtpPort, SmtpPort, "TCP", "KliveMail SMTP");
                    await ServiceLog(added
                        ? "KliveMail: opened port 25 via UPnP."
                        : "KliveMail: port 25 already forwarded, or no UPnP gateway (forward it manually on the router).");
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveMail: UPnP port-forward attempt failed (forward port 25 manually).", false);
            }
        }

        private async Task RunSmtpServerAsync(CancellationToken ct)
        {
            try
            {
                X509Certificate2? cert = await WaitForCertificateAsync(ct);

                var options = new SmtpServerOptionsBuilder()
                    .ServerName(KliveMailRepository.MailDomain)
                    .Endpoint(builder =>
                    {
                        builder.Port(SmtpPort);
                        builder.AllowUnsecureAuthentication(true);
                        if (cert != null)
                            builder.Certificate(cert);
                    })
                    .Build();

                await ServiceLog(cert != null
                    ? "KliveMail SMTP: STARTTLS enabled (reusing klive.dev certificate)."
                    : "KliveMail SMTP: no certificate available — running plaintext (senders may still deliver).");

                var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
                serviceProvider.Add(new KliveMailMessageStore(this, Repo));
                serviceProvider.Add(new KliveMailMailboxFilter());

                smtpServer = new SmtpServer.SmtpServer(options, serviceProvider);
                await smtpServer.StartAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Service shutting down — expected.
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveMail SMTP server crashed.");
            }
        }

        // KliveAPI creates/loads the TLS PFX during its own startup; give it a short window.
        private async Task<X509Certificate2?> WaitForCertificateAsync(CancellationToken ct)
        {
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    if (await ExecuteServiceMethod<KliveApiService>("GetServerCertificate") is X509Certificate2 cert)
                        return cert;
                }
                catch { }
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
            return null;
        }

        // Called by the message store once a message is persisted.
        public async Task NotifyMailReceived(IEnumerable<string> recipients, string from, string subject)
            => await ServiceLog($"KliveMail received mail for [{string.Join(", ", recipients)}] from {from}: {subject}");

        public async Task LogStoreError(Exception ex)
            => await ServiceLogError(ex, "KliveMail: error storing inbound message.");
    }
}
