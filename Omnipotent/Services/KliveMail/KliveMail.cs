using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
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
        private readonly SemaphoreSlim statsRefreshRequested = new(0, 1);
        private MailSummarySnapshot? currentSummary;
        private readonly string? statsSnapshotPathOverride;
        private DateTime lastSmtpAlertUtc = DateTime.MinValue;

        /// <summary>Every change queues a refresh; this is only how long an idle summary may sit.</summary>
        private static readonly TimeSpan IdleRefresh = TimeSpan.FromSeconds(30);

        public KliveMailHealth Health { get; } = new();

        internal string? StatsSnapshotJson => FreshSummary()?.StatsJson;
        internal string? MailboxesSnapshotJson => FreshSummary()?.MailboxesJson;

        private string StatsSnapshotPath => statsSnapshotPathOverride ?? Path.Combine(
            OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveMailDirectory), "mail-summary.snapshot.json");

        private MailSummarySnapshot? FreshSummary()
        {
            var snapshot = Volatile.Read(ref currentSummary);
            if (snapshot == null) return null;
            TimeSpan age = DateTime.UtcNow - snapshot.GeneratedAtUtc;
            return age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(2) ? snapshot : null;
        }

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
                Health.Started();
                Db = new KliveMailDb();
                await Db.InitialiseAsync();
                Repo = new KliveMailRepository(Db);

                await LoadStatsSnapshotAsync();
                _ = Task.Run(() => RefreshStatsLoopAsync(cancellationToken.Token));

                routes = new KliveMailRoutes(this);
                await routes.RegisterRoutes();

                // Previews and codes for mail stored before the digest existed (or under older
                // rules). Behind the routes: until it reaches a row the list shows it without one.
                _ = Task.Run(() => BackfillDigestsAsync(cancellationToken.Token));

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

        /// <summary>
        /// Runs the SMTP listener for the life of the service and restarts it if it ever fails or
        /// returns (15 s, doubling to 10 min). It used to log one error and stay down until the next
        /// process start, with nothing on any page saying mail had stopped arriving.
        /// </summary>
        private async Task RunSmtpServerAsync(CancellationToken ct)
        {
            X509Certificate2? cert;
            try { cert = await WaitForCertificateAsync(ct); }
            catch (OperationCanceledException) { return; }

            await ServiceLog(cert != null
                ? "KliveMail SMTP: STARTTLS enabled (reusing klive.dev certificate)."
                : "KliveMail SMTP: no certificate available — running plaintext (senders may still deliver).");

            var delay = TimeSpan.FromSeconds(15);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var server = CreateSmtpServer(SmtpPort, cert, new KliveMailMessageStore(this, Repo));
                    server.SessionCreated += (_, _) => Health.SessionStarted();
                    server.SessionFaulted += (_, _) => Health.SessionFaulted();
                    smtpServer = server;
                    Health.SmtpListening(cert != null);
                    await server.StartAsync(ct);
                    if (ct.IsCancellationRequested) break;
                    Health.SmtpStopped();
                    await ServiceLogError(new InvalidOperationException("The SMTP listener returned without being stopped."),
                        $"KliveMail SMTP listener stopped; restarting in {delay.TotalSeconds:0} s.", false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Health.SmtpFailed(ex.GetType().Name + ": " + ex.Message);
                    await ServiceLogError(ex, $"KliveMail SMTP listener failed; restarting in {delay.TotalSeconds:0} s.");
                    await AlertSmtpDownAsync(ex.Message);
                }

                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 600));
                Health.SmtpRestarting();
                QueueStatsRefresh();
            }
        }

        /// <summary>
        /// The SMTP server as KliveMail runs it: catch-all for @klive.dev, never a relay, no logins
        /// (KliveMailReceiveOnly), STARTTLS when the site's certificate is available.
        /// </summary>
        internal static SmtpServer.SmtpServer CreateSmtpServer(int port, X509Certificate2? cert, SmtpServer.Storage.IMessageStore store,
            System.Net.IPAddress? bindTo = null)
        {
            var options = new SmtpServerOptionsBuilder()
                .ServerName(KliveMailRepository.MailDomain)
                .Endpoint(builder =>
                {
                    // Every interface in production; tests bind loopback only.
                    if (bindTo != null) builder.Endpoint(new System.Net.IPEndPoint(bindTo, port));
                    else builder.Port(port);
                    builder.AllowUnsecureAuthentication(false);
                    if (cert != null)
                        builder.Certificate(cert);
                })
                .Build();

            var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
            serviceProvider.Add(store);
            serviceProvider.Add(new KliveMailMailboxFilter());
            serviceProvider.Add(KliveMailReceiveOnly.RefuseAllLogins);
            serviceProvider.Add(new KliveMailReceiveOnly.CommandFactory());
            return new SmtpServer.SmtpServer(options, serviceProvider);
        }

        private async Task AlertSmtpDownAsync(string reason)
        {
            if (DateTime.UtcNow - lastSmtpAlertUtc < TimeSpan.FromHours(6)) return;
            lastSmtpAlertUtc = DateTime.UtcNow;
            try
            {
                await ExecuteServiceMethod<KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives",
                    $"KliveMail is not receiving mail: the SMTP listener on port {SmtpPort} failed ({reason}). It retries on its own, up to every 10 minutes; /klivemail shows its state.");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveMail couldn't send the SMTP-down alert.", false);
            }
        }

        private async Task BackfillDigestsAsync(CancellationToken ct)
        {
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int built = await Repo.BackfillDigestsAsync(ct);
                Health.DigestsBuilt(built, watch.ElapsedMilliseconds);
                if (built > 0)
                {
                    QueueStatsRefresh();
                    await ServiceLog($"KliveMail: built previews and codes for {built} message(s) in {watch.ElapsedMilliseconds} ms.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveMail: could not build message previews; lists show those messages without one.", false);
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
            Health.MessageStored();
            QueueStatsRefresh();
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
                QueueStatsRefresh();
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
        {
            Health.StoreFailed(ex.GetType().Name + ": " + ex.Message);
            QueueStatsRefresh();
            await ServiceLogError(ex, "KliveMail: error storing inbound message.");
        }

        internal void QueueStatsRefresh()
        {
            try { statsRefreshRequested.Release(); }
            catch (SemaphoreFullException) { /* A refresh is already queued. */ }
        }

        internal KliveMail(string snapshotPath) : this() => statsSnapshotPathOverride = snapshotPath;

        internal async Task LoadStatsSnapshotAsync()
        {
            try
            {
                if (!File.Exists(StatsSnapshotPath)) return;
                var saved = JsonConvert.DeserializeObject<MailSummarySnapshot>(
                    await File.ReadAllTextAsync(StatsSnapshotPath));
                if (string.IsNullOrWhiteSpace(saved?.StatsJson) || string.IsNullOrWhiteSpace(saved.MailboxesJson)
                    || saved.GeneratedAtUtc > DateTime.UtcNow.AddMinutes(1)
                    || DateTime.UtcNow - saved.GeneratedAtUtc > TimeSpan.FromMinutes(2))
                    return;
                Volatile.Write(ref currentSummary, saved);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveMail: could not load the saved mail summary.", false);
            }
        }

        private async Task RefreshStatsLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Both database reads and JSON creation happen only on this background task.
                    var snapshot = BuildSnapshot(await Repo.GetSummaryAsync(DateTime.UtcNow, ct), Health.View(), DateTime.UtcNow);
                    string path = StatsSnapshotPath;
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllTextAsync(temporary, JsonConvert.SerializeObject(snapshot), ct);
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                    }

                    // Strings are immutable. A request takes one atomic reference read and never
                    // opens SQLite or waits for a refresh to complete.
                    Volatile.Write(ref currentSummary, snapshot);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, "KliveMail: could not refresh the mail summary.", false);
                }

                try
                {
                    await statsRefreshRequested.WaitAsync(IdleRefresh, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }

        /// <summary>
        /// The two summary documents the routes hand out. /klivemail/stats stays small (the dashboard
        /// reads it); /klivemail/mailboxes carries everything the mail page's rail and header show.
        /// The first three fields of each keep their old names, for pages from before this.
        /// </summary>
        internal static MailSummarySnapshot BuildSnapshot(Models.MailSummary s, object health, DateTime nowUtc)
        {
            return new MailSummarySnapshot
            {
                GeneratedAtUtc = nowUtc,
                StatsJson = JsonConvert.SerializeObject(new
                {
                    total = s.Total, unread = s.Unread, trash = s.Trash,
                    lastReceivedUtc = s.LastReceivedUtc, received24h = s.Received24h, received7d = s.Received7d,
                }),
                MailboxesJson = JsonConvert.SerializeObject(new
                {
                    all = new { total = s.Total, unread = s.Unread },
                    trash = s.Trash,
                    mailboxes = s.Mailboxes,
                    others = s.Others,
                    daily = s.Daily,
                    lastReceivedUtc = s.LastReceivedUtc,
                    received24h = s.Received24h,
                    received7d = s.Received7d,
                    health,
                    generatedAtUtc = nowUtc,
                }),
            };
        }

        internal sealed class MailSummarySnapshot
        {
            public DateTime GeneratedAtUtc { get; set; }
            public string? StatsJson { get; set; }
            public string? MailboxesJson { get; set; }
        }
    }
}
