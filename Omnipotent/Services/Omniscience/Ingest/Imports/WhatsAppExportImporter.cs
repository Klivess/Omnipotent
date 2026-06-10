using Omnipotent.Services.Omniscience.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Ingest.Imports
{
    /// <summary>
    /// Imports WhatsApp chat exports (Export Chat → without media → _chat.txt).
    /// Handles both export dialects:
    ///   iOS:     [12/05/2024, 14:23:11] Name: message
    ///   Android: 12/05/2024, 14:23 - Name: message
    /// Continuation lines append to the previous message. Senders have no stable ids in
    /// exports, so identities are keyed by name slug; message ids are deterministic
    /// hashes so re-importing the same export is a no-op (pipeline dedupe).
    /// Dates are parsed day-first (dd/MM/yyyy — UK convention).
    /// </summary>
    public class WhatsAppExportImporter
    {
        private static readonly Regex IosLine = new(
            @"^\[(?<date>\d{1,2}/\d{1,2}/\d{2,4}),\s*(?<time>\d{1,2}:\d{2}(?::\d{2})?)\]\s*(?<name>[^:]+):\s?(?<text>.*)$",
            RegexOptions.Compiled);
        private static readonly Regex AndroidLine = new(
            @"^(?<date>\d{1,2}/\d{1,2}/\d{2,4}),\s*(?<time>\d{1,2}:\d{2}(?::\d{2})?)\s*-\s*(?<name>[^:]+):\s?(?<text>.*)$",
            RegexOptions.Compiled);
        private static readonly string[] DateFormats =
        {
            "d/M/yyyy H:mm:ss", "d/M/yyyy H:mm", "d/M/yy H:mm:ss", "d/M/yy H:mm",
        };

        private readonly Omniscience service;
        private readonly IngestPipeline pipeline;

        public WhatsAppExportImporter(Omniscience service, IngestPipeline pipeline)
        {
            this.service = service;
            this.pipeline = pipeline;
        }

        public async Task<int> ImportAsync(string txtPath, CancellationToken ct)
        {
            string chatName = Path.GetFileNameWithoutExtension(txtPath)
                .Replace("WhatsApp Chat with ", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_chat", "").Trim();
            if (chatName.Length == 0) chatName = "whatsapp-import";
            string channelId = "wa-" + Slug(chatName);

            // Parse all lines into discrete messages first (continuations fold in).
            var parsed = new List<(DateTime SentAt, string Sender, StringBuilder Text)>();
            foreach (var line in File.ReadLines(txtPath))
            {
                ct.ThrowIfCancellationRequested();
                string clean = line.TrimStart('‎', '‏', '﻿'); // strip LTR/RTL marks
                var match = IosLine.Match(clean);
                if (!match.Success) match = AndroidLine.Match(clean);
                if (match.Success && TryParseDate(match.Groups["date"].Value, match.Groups["time"].Value, out var sentAt))
                {
                    string sender = match.Groups["name"].Value.Trim();
                    string text = match.Groups["text"].Value;
                    if (sender.Length is 0 or > 60) continue;
                    parsed.Add((sentAt, sender, new StringBuilder(text)));
                }
                else if (parsed.Count > 0)
                {
                    parsed[^1].Text.AppendLine().Append(clean); // continuation of the previous message
                }
            }
            if (parsed.Count == 0) throw new InvalidDataException("No parseable WhatsApp messages found.");

            var senders = parsed.Select(p => p.Sender).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string kind = senders.Count > 2 ? "group_dm" : "dm";

            int imported = 0;
            foreach (var (sentAt, sender, text) in parsed)
            {
                ct.ThrowIfCancellationRequested();
                string content = text.ToString().Trim();
                if (content.Length == 0 || content is "<Media omitted>" or "image omitted" or "video omitted") continue;

                var hm = new HarvestedMessage
                {
                    Platform = "whatsapp",
                    PlatformMessageId = DeterministicId(channelId, sentAt, sender, content),
                    SentAt = sentAt,
                    Content = content,
                    ConversationKind = kind,
                    ChannelId = channelId,
                    ChannelTitle = chatName,
                    Author = new HarvestedIdentity
                    {
                        Platform = "whatsapp",
                        PlatformUserId = "wa-" + Slug(sender),
                        Username = sender,
                        DisplayName = sender,
                    },
                };
                try
                {
                    await pipeline.IngestAsync(hm, ct);
                    imported++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] WhatsApp message ingest failed"); }
            }
            return imported;
        }

        private static bool TryParseDate(string date, string time, out DateTime result)
        {
            string combined = date + " " + time;
            foreach (var fmt in DateFormats)
                if (DateTime.TryParseExact(combined, fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
                    return true;
            result = default;
            return false;
        }

        private static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

        // Deterministic per-message id → re-imports dedupe via the normal pipeline path.
        private static string DeterministicId(string channelId, DateTime sentAt, string sender, string content)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(
                channelId + "|" + sentAt.Ticks + "|" + sender + "|" + content[..Math.Min(content.Length, 80)]));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        }
    }
}
