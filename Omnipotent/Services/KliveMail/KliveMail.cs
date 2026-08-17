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

        /// <summary>
        /// Raised once for each stored inbound message (one per envelope recipient). Lets other
        /// services observe inbound mail without polling — e.g. the Projects stimulus bus wakes a
        /// project when a matching email arrives. Subscribers must not throw; exceptions are logged.
        /// </summary>
        public event Action<Models.StoredMessage>? MailStored;

        // Called by the message store once a message is persisted.
        internal void RaiseMailStored(Models.StoredMessage message)
        {
            var handler = MailStored;
            if (handler == null) return;
            try { handler(message); }
            catch (Exception ex) { _ = ServiceLogError(ex, "KliveMail: a MailStored subscriber threw.", false); }
        }

        // ── outbound ──────────────────────────────────────────────────────────────────────────

        private Omnipotent.Services.AccountRegistry.AccountRegistry? GetAccountRegistry()
            => GetActiveServices().OfType<Omnipotent.Services.AccountRegistry.AccountRegistry>()
                .FirstOrDefault(s => s.IsServiceActive());

        /// <summary>
        /// Sends mail from a @klive.dev address through the relay registered in the shared account
        /// registry, and files a copy in the sending mailbox so the send is auditable afterwards.
        /// Throws with an actionable message when no relay is registered — an agent that cannot
        /// send must know that, not silently believe it did.
        /// </summary>
        public async Task<string> SendMailAsync(
            string? from, string toRaw, string subject, string body, bool isHtml = false,
            string? ccRaw = null, string? replyTo = null, IReadOnlyList<string>? attachments = null,
            string? owner = null, CancellationToken ct = default)
        {
            var to = KliveMailSender.ParseRecipients(toRaw);
            var cc = KliveMailSender.ParseRecipients(ccRaw);
            if (to.Count == 0)
                throw new ArgumentException("Provide at least one recipient address in 'to'.");
            foreach (string address in to.Concat(cc))
                if (!KliveMailSender.IsValidAddress(address))
                    throw new ArgumentException($"'{address}' is not a valid email address.");
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Provide a 'subject' — relays and spam filters treat empty subjects harshly.");
            if (string.IsNullOrWhiteSpace(body))
                throw new ArgumentException("Provide a 'body'.");

            var relay = ResolveRelay(from, owner)
                ?? throw new InvalidOperationException(KliveMailSender.NoRelayConfiguredMessage);

            string sender = KliveMailSender.IsValidAddress(from) ? from!.Trim() : relay.FromAddress;
            var files = (attachments ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()).ToList();
            foreach (string path in files)
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Attachment '{path}' does not exist on the host.");

            string outcome = await KliveMailSender.SendAsync(
                relay, sender, to, cc, subject, body, isHtml, files, replyTo, ct);
            await ServiceLog($"KliveMail sent mail from {sender} to [{string.Join(", ", to)}] via {relay.Host}: {subject}");
            await FileSentCopyAsync(sender, to, cc, subject, body, isHtml, files.Count > 0, ct);
            return outcome;
        }

        private KliveMailSender.RelaySettings? ResolveRelay(string? preferredFrom, string? owner)
        {
            var registry = GetAccountRegistry();
            if (registry == null) return null;
            foreach (string serviceKey in KliveMailSender.RelayServiceKeys)
            {
                foreach (var account in registry.List(serviceKey))
                {
                    string? ReadSecret(string field)
                    {
                        // The username-qualified form is unambiguous even with several accounts on
                        // the same provider, and keeps decryption on the host side.
                        var result = registry.TryResolveForTyping(
                            "{account:" + serviceKey + "/" + account.Username + "/" + field + "}", owner);
                        if (result.Error != null) return null;
                        return result.Text.Contains("{account:", StringComparison.OrdinalIgnoreCase)
                            ? null : result.Text;
                    }
                    var settings = KliveMailSender.FromAccount(account, ReadSecret, preferredFrom);
                    if (settings != null) return settings;
                }
            }
            return null;
        }

        /// <summary>A sent copy in the sender's own mailbox is the evidence that the send happened;
        /// without it an agent's only record of an outbound email is its own claim.</summary>
        private async Task FileSentCopyAsync(
            string sender, List<string> to, List<string> cc, string subject, string body,
            bool isHtml, bool hadAttachments, CancellationToken ct)
        {
            try
            {
                string recipients = "To: " + string.Join(", ", to)
                    + (cc.Count > 0 ? "\nCc: " + string.Join(", ", cc) : "");
                var copy = new Models.StoredMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ToAddress = KliveMailRepository.NormalizeAddress(sender),
                    FromAddress = sender,
                    FromName = "Sent by KliveMail",
                    Subject = "[sent] " + subject,
                    DateUtc = DateTime.UtcNow,
                    ReceivedUtc = DateTime.UtcNow,
                    ThreadId = Guid.NewGuid().ToString("N"),
                    BodyText = recipients + "\n\n" + (isHtml ? "(HTML body)\n\n" : "") + body,
                    BodyHtml = isHtml ? body : null,
                    HasAttachments = hadAttachments,
                    IsRead = true,
                };
                copy.RawSize = (copy.BodyText ?? "").Length;
                await Repo.InsertMessageAsync(copy, ct);
            }
            catch (Exception ex)
            {
                // The mail really was accepted by the relay; failing to file the copy must not
                // turn a successful send into a reported failure.
                await ServiceLogError(ex, "KliveMail: could not file a copy of a sent message.", false);
            }
        }

        // Called by the message store once a message is persisted.
        public async Task NotifyMailReceived(IEnumerable<string> recipients, string from, string subject)
            => await ServiceLog($"KliveMail received mail for [{string.Join(", ", recipients)}] from {from}: {subject}");

        public async Task LogStoreError(Exception ex)
            => await ServiceLogError(ex, "KliveMail: error storing inbound message.");
    }
}
