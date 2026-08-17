using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Omnipotent.Services.AccountRegistry;

namespace Omnipotent.Services.KliveMail
{
    /// <summary>
    /// Outbound mail for @klive.dev through a relay whose credentials live in the shared account
    /// registry. KliveMail's own SMTP server is receive-only, so before this existed an agent could
    /// read verification codes but could not answer a single email — which quietly made whole
    /// strategies (outreach, applications, replies) impossible to execute.
    ///
    /// Delivery goes through a provider relay rather than direct-to-MX on purpose: mail sent
    /// straight from a residential address is refused or silently binned by nearly every receiver,
    /// so "sent" would be a lie. A relay makes the send verifiable.
    /// </summary>
    public static class KliveMailSender
    {
        public sealed record RelaySettings(string Host, int Port, string Username, string Password, string FromAddress, string ServiceKey);

        /// <summary>Registry services checked, in order, for relay credentials.</summary>
        public static readonly IReadOnlyList<string> RelayServiceKeys =
            new[] { "smtp", "sendgrid", "mailgun", "brevo", "sendinblue", "resend", "mailjet", "postmark", "zoho", "gmail" };

        /// <summary>Sensible host/port for providers whose account only stores an API key.</summary>
        public static (string Host, int Port, string Username)? WellKnownRelay(string serviceKey) =>
            serviceKey.ToLowerInvariant() switch
            {
                "sendgrid" => ("smtp.sendgrid.net", 587, "apikey"),
                "mailgun" => ("smtp.mailgun.org", 587, ""),
                "brevo" or "sendinblue" => ("smtp-relay.brevo.com", 587, ""),
                "resend" => ("smtp.resend.com", 587, "resend"),
                "mailjet" => ("in-v3.mailjet.com", 587, ""),
                "postmark" => ("smtp.postmarkapp.com", 587, ""),
                "zoho" => ("smtp.zoho.com", 587, ""),
                "gmail" => ("smtp.gmail.com", 587, ""),
                _ => null,
            };

        private static readonly Regex AddressPattern = new(
            @"^[^@\s<>,;]+@[^@\s<>,;]+\.[A-Za-z]{2,}$", RegexOptions.Compiled);

        public static bool IsValidAddress(string? address) =>
            !string.IsNullOrWhiteSpace(address) && AddressPattern.IsMatch(address.Trim());

        /// <summary>Splits a comma/semicolon/whitespace separated recipient list.</summary>
        public static List<string> ParseRecipients(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            return raw.Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().Trim('<', '>'))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Assembles relay settings from one registry account. Host/port may be stored as secrets,
        /// as plaintext notes, or be implied by the provider; the username defaults to the
        /// account's own username and the sender address to its email.
        /// </summary>
        public static RelaySettings? FromAccount(
            RegisteredAccount account, Func<string, string?> readSecret, string? fallbackFrom)
        {
            string serviceKey = (account.ServiceKey ?? "").ToLowerInvariant();
            var wellKnown = WellKnownRelay(serviceKey);

            string host = FirstNonEmpty(readSecret("host"), readSecret("smtpHost"), readSecret("server"),
                ValueFromNotes(account.Notes, "host"), wellKnown?.Host);
            string password = FirstNonEmpty(readSecret("password"), readSecret("apiKey"), readSecret("api_key"),
                readSecret("key"), readSecret("token"), readSecret("smtpPassword"));
            string username = FirstNonEmpty(readSecret("username"), readSecret("user"), readSecret("login"),
                ValueFromNotes(account.Notes, "username"), account.Username, wellKnown?.Username);
            string from = FirstNonEmpty(readSecret("from"), ValueFromNotes(account.Notes, "from"),
                account.Email, fallbackFrom);
            string portText = FirstNonEmpty(readSecret("port"), readSecret("smtpPort"),
                ValueFromNotes(account.Notes, "port"), wellKnown?.Port.ToString());

            if (host.Length == 0 || password.Length == 0 || !IsValidAddress(from)) return null;
            if (!int.TryParse(portText, out int port) || port <= 0 || port > 65535) port = 587;
            return new RelaySettings(host, port, username, password, from.Trim(), serviceKey);
        }

        /// <summary>The message an agent gets when no relay is registered. It must be enough to fix
        /// the situation without a human, because sending mail is core project work.</summary>
        public const string NoRelayConfiguredMessage =
            "KliveMail can receive but cannot send yet: no outbound relay is registered. Fix it yourself — " +
            "sign up for any free transactional-mail provider (Brevo, Resend, SendGrid, Mailjet all have free " +
            "tiers), verify the sender using a @klive.dev mailbox you create with klivemail op:create_mailbox, " +
            "then store it with account op:register — service 'smtp' (or the provider's name), username = the " +
            "SMTP login, email = the verified From address, and secrets { host, port, password }. Every project " +
            "shares it from then on. Until then, do not claim any email has been sent.";

        public static async Task<string> SendAsync(
            RelaySettings relay, string from, IReadOnlyList<string> to, IReadOnlyList<string> cc,
            string subject, string body, bool isHtml, IReadOnlyList<string> attachments,
            string? replyTo, CancellationToken ct)
        {
            using var message = new MailMessage
            {
                From = new MailAddress(from),
                Subject = subject,
                Body = body,
                IsBodyHtml = isHtml,
            };
            foreach (string address in to) message.To.Add(address);
            foreach (string address in cc) message.CC.Add(address);
            if (IsValidAddress(replyTo)) message.ReplyToList.Add(new MailAddress(replyTo!.Trim()));

            var opened = new List<FileStream>();
            try
            {
                foreach (string path in attachments)
                {
                    var stream = File.OpenRead(path);
                    opened.Add(stream);
                    message.Attachments.Add(new Attachment(stream, Path.GetFileName(path)));
                }

                using var client = new SmtpClient(relay.Host, relay.Port)
                {
                    EnableSsl = true,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 60_000,
                    Credentials = string.IsNullOrWhiteSpace(relay.Username)
                        ? new NetworkCredential(from, relay.Password)
                        : new NetworkCredential(relay.Username, relay.Password),
                };
                using (ct.Register(() => { try { client.SendAsyncCancel(); } catch { } }))
                    await client.SendMailAsync(message, ct);
            }
            finally
            {
                foreach (var stream in opened) { try { stream.Dispose(); } catch { } }
            }

            return $"Sent to {string.Join(", ", to)}"
                + (cc.Count > 0 ? $" (cc {string.Join(", ", cc)})" : "")
                + $" from {from} via {relay.Host}"
                + (attachments.Count > 0 ? $" with {attachments.Count} attachment(s)" : "")
                + ". Delivery was accepted by the relay; a bounce would arrive back in the sending mailbox.";
        }

        private static string FirstNonEmpty(params string?[] candidates)
        {
            foreach (string? candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
            return "";
        }

        /// <summary>Agents habitually record connection details as "host=... port=..." in notes.</summary>
        internal static string? ValueFromNotes(string? notes, string key)
        {
            if (string.IsNullOrWhiteSpace(notes)) return null;
            var match = Regex.Match(notes, $@"\b{Regex.Escape(key)}\s*[:=]\s*([^\s,;]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
