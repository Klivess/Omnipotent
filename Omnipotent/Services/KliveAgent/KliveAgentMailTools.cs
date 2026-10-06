using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Omnipotent.Services.KliveMail.Models;
using Omnipotent.Services.KliveMail.Persistence;
using Omnipotent.Services.Projects;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// First-class KliveMail tools for KliveAgent: create an @klive.dev inbox, read it, and wait for a
/// verification email. KliveMail is catch-all, so any address works the moment it is used, but a
/// named mailbox shows up properly in the mail client.
///
/// Without these the only path was reflecting into the KliveMail service from execute_csharp — the
/// Service Surface exposes it read-only, so creating a mailbox meant guessing at repository internals
/// mid-signup. Projects hit exactly that (a 730k-token reflection loop) before it got first-class
/// tools; this is the same fix for the interactive agent, driven in-process with no HTTP or auth.
/// </summary>
internal static class KliveAgentMailTools
{
    internal const int MaxWaitSeconds = 900;

    private static KliveMailRepository? Repo(KliveAgent agent, out string? error)
    {
        var mail = agent.GetActiveServices().OfType<KliveMail.KliveMail>().FirstOrDefault(s => s.IsServiceActive());
        if (mail?.Repo == null)
        {
            error = "KliveMail is not running, so @klive.dev mail cannot be read or created right now.";
            return null;
        }
        error = null;
        return mail.Repo;
    }

    internal static string NormalizeKliveMailAddress(string address, out string? error)
    {
        string normalized = KliveMailRepository.NormalizeAddress(address ?? "");
        error = normalized.EndsWith("@" + KliveMailRepository.MailDomain, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"'{normalized}' is not a KliveMail address — KliveMail only receives @{KliveMailRepository.MailDomain}.";
        return normalized;
    }

    internal static async Task<string> CreateMailboxAsync(KliveAgent agent, string? address, string? displayName, CancellationToken ct)
    {
        var repo = Repo(agent, out var error);
        if (repo == null) return error!;
        if (string.IsNullOrWhiteSpace(address))
            return $"Error: provide an 'address' (e.g. 'tumblr.klives' → tumblr.klives@{KliveMailRepository.MailDomain}).";
        string normalized = NormalizeKliveMailAddress(address, out var addressError);
        if (addressError != null) return "Error: " + addressError;
        bool created = await repo.CreateMailboxAsync(normalized, string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(), ct);
        return (created ? $"Created mailbox {normalized}." : $"Mailbox {normalized} already exists.")
            + " It receives mail immediately. Use this exact address on the site, then klivemail_wait_for_email to collect the verification email.";
    }

    internal static async Task<string> ListMessagesAsync(KliveAgent agent, string? mailbox, int limit, bool unreadOnly, CancellationToken ct)
    {
        var repo = Repo(agent, out var error);
        if (repo == null) return error!;
        string? box = null;
        if (!string.IsNullOrWhiteSpace(mailbox))
        {
            box = NormalizeKliveMailAddress(mailbox, out var addressError);
            if (addressError != null) return "Error: " + addressError;
        }
        var messages = await repo.ListMessagesAsync(box, unreadOnly, false, false, 1, Math.Clamp(limit, 1, 100), ct);
        if (messages.Count == 0) return box == null ? "No messages in KliveMail." : $"No messages for {box} yet.";
        var sb = new StringBuilder();
        sb.AppendLine($"{messages.Count} message(s){(box == null ? "" : " for " + box)}, newest first:");
        foreach (var m in messages)
            sb.AppendLine($"- id={m.Id} | {Data_Handling.TemporalFormat.StampMinute(m.ReceivedUtc)} | from {m.FromAddress} → {m.ToAddress} | "
                + $"{(m.IsRead ? "" : "[unread] ")}{Clip(m.Subject ?? "(no subject)", 90)} — {Clip(m.Snippet ?? "", 110)}");
        return sb.ToString().TrimEnd();
    }

    internal static async Task<string> GetMessageAsync(KliveAgent agent, string? id, CancellationToken ct)
    {
        var repo = Repo(agent, out var error);
        if (repo == null) return error!;
        if (string.IsNullOrWhiteSpace(id)) return "Error: provide the message 'id' (from klivemail_list_messages).";
        var message = await repo.GetMessageAsync(id.Trim(), ct);
        return message == null ? $"No message with id '{id}'." : DescribeMessage(message, maxBodyChars: 6000);
    }

    /// <summary>
    /// Blocks until an email matching the filters arrives (or one arrived shortly before this call),
    /// then returns it with any verification code and the links most likely to be the verification
    /// link. Also catches the near-miss where the site normalised the address (dots, one typo) and the
    /// mail landed in a sibling @klive.dev mailbox.
    /// </summary>
    internal static async Task<string> WaitForEmailAsync(KliveAgent agent, string? mailbox, string? fromContains,
        string? subjectContains, int timeoutSeconds, int lookbackSeconds, CancellationToken ct)
    {
        var repo = Repo(agent, out var error);
        if (repo == null) return error!;
        if (string.IsNullOrWhiteSpace(mailbox)) return "Error: provide the 'mailbox' you signed up with.";
        string box = NormalizeKliveMailAddress(mailbox, out var addressError);
        if (addressError != null) return "Error: " + addressError;

        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, MaxWaitSeconds);
        lookbackSeconds = Math.Clamp(lookbackSeconds, 0, 3600);
        DateTime floor = DateTime.UtcNow.AddSeconds(-lookbackSeconds);
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        bool Matches(MessageSummary m) =>
            m.ReceivedUtc >= floor
            && (string.IsNullOrWhiteSpace(fromContains)
                || (m.FromAddress ?? "").Contains(fromContains, StringComparison.OrdinalIgnoreCase)
                || (m.FromName ?? "").Contains(fromContains, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(subjectContains)
                || (m.Subject ?? "").Contains(subjectContains, StringComparison.OrdinalIgnoreCase));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var own = (await repo.ListMessagesAsync(box, false, false, false, 1, 25, ct)).Where(Matches).ToList();
            var hit = own.OrderBy(m => m.ReceivedUtc).FirstOrDefault();
            string? mismatchNote = null;
            if (hit == null)
            {
                var nearby = await repo.ListMessagesAsync(null, false, false, false, 1, 100, ct);
                hit = nearby.Where(m => Matches(m)
                        && !string.Equals(m.ToAddress, box, StringComparison.OrdinalIgnoreCase)
                        && ProjectCommanderTools.LikelyMailboxVariant(box, m.ToAddress))
                    .OrderBy(m => m.ReceivedUtc)
                    .FirstOrDefault();
                if (hit != null)
                    mismatchNote = $"ADDRESS MISMATCH: this arrived at {hit.ToAddress}, not {box} — the site stored the address differently; use {hit.ToAddress} from now on.";
            }
            if (hit != null)
            {
                var full = await repo.GetMessageAsync(hit.Id, ct);
                if (full != null)
                {
                    try { await repo.SetReadAsync(full.Id, true, ct); } catch { }
                    string described = DescribeMessage(full, maxBodyChars: 2500);
                    return mismatchNote == null ? described : mismatchNote + "\n" + described;
                }
            }
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }

        return $"No matching email reached {box} within {timeoutSeconds}s"
            + (string.IsNullOrWhiteSpace(fromContains) ? "" : $" (from containing '{fromContains}')")
            + ". KliveMail itself answered; the site may not have sent it yet, may need its 'resend' button, or may have rejected the address. "
            + "Check the page for an error, use the site's resend, then wait again (klivemail_list_messages shows everything that did arrive).";
    }

    /// <summary>Headers, verification code, ranked links and a bounded body for one message.</summary>
    internal static string DescribeMessage(StoredMessage m, int maxBodyChars)
    {
        string text = !string.IsNullOrWhiteSpace(m.BodyText) ? m.BodyText! : HtmlToText(m.BodyHtml);
        string? code = ProjectCommanderTools.ExtractVerificationCode((m.Subject ?? "") + "\n" + text);
        var links = ExtractLinks(m.BodyHtml, m.BodyText);

        var sb = new StringBuilder();
        sb.AppendLine($"Email id={m.Id}");
        sb.AppendLine($"From: {(string.IsNullOrWhiteSpace(m.FromName) ? "" : m.FromName + " ")}<{m.FromAddress}>");
        sb.AppendLine($"To: {m.ToAddress}");
        sb.AppendLine($"Received: {Data_Handling.TemporalFormat.StampMinute(m.ReceivedUtc)}");
        sb.AppendLine($"Subject: {m.Subject}");
        if (code != null) sb.AppendLine($"Verification code (best guess): {code}");
        if (links.Count > 0)
        {
            sb.AppendLine("Links (most likely verification link first) — open the right one with computer_navigate:");
            foreach (var link in links) sb.AppendLine("  " + link);
        }
        sb.AppendLine();
        sb.Append(Clip(text.Trim(), maxBodyChars));
        return sb.ToString().TrimEnd();
    }

    private static readonly Regex HrefPattern = new(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareUrlPattern = new(@"https?://[^\s<>""'\)\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] VerificationCues = { "verify", "verif", "confirm", "activate", "activation", "validate", "email", "token", "auth", "magic", "login", "signin", "sign-in", "register", "welcome", "complete" };
    private static readonly string[] NoiseCues = { "unsubscribe", "privacy", "terms", "help", "support", "preferences", "policy", "facebook.com", "twitter.com", "instagram.com", "apple.com/app", "play.google", "mailto:" };

    /// <summary>Unique http(s) links from the HTML and text bodies, verification-looking ones first,
    /// footer noise (unsubscribe, privacy, app stores, socials) last.</summary>
    internal static List<string> ExtractLinks(string? html, string? text, int max = 8)
    {
        var found = new List<string>();
        void Add(string raw)
        {
            string url = WebUtility.HtmlDecode(raw).Trim().TrimEnd('.', ',', ';');
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            if (url.Length > 2000) return;
            if (!found.Contains(url, StringComparer.Ordinal)) found.Add(url);
        }
        if (!string.IsNullOrEmpty(html))
            foreach (Match match in HrefPattern.Matches(html)) Add(match.Groups[1].Value);
        foreach (var source in new[] { text, html })
            if (!string.IsNullOrEmpty(source))
                foreach (Match match in BareUrlPattern.Matches(source)) Add(match.Value);

        int Score(string url)
        {
            string lower = url.ToLowerInvariant();
            int score = VerificationCues.Count(cue => lower.Contains(cue)) * 10;
            if (NoiseCues.Any(cue => lower.Contains(cue))) score -= 50;
            return score;
        }
        return found
            .Select((url, index) => (url, index, score: Score(url)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(max)
            .Select(x => x.url)
            .ToList();
    }

    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        string s = Regex.Replace(html, "(?is)<(style|script)[^>]*>.*?</\\1>", " ");
        s = Regex.Replace(s, "(?is)<br\\s*/?>|</(p|div|li|tr|h[1-6]|table)>", "\n");
        s = Regex.Replace(s, "(?s)<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, "[ \\t]+", " ");
        s = Regex.Replace(s, "\\n\\s*\\n+", "\n");
        return s.Trim();
    }

    private static string Clip(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max] + "…";
}
