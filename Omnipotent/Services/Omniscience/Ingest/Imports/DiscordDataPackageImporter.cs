using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Ingest.Imports
{
    /// <summary>
    /// Imports a Discord GDPR data package (the zip from Settings → Privacy → Request
    /// Data). It contains the package owner's complete authored history — including old
    /// DMs no live token can reach any more. Structure: messages/index.json plus one
    /// folder per channel with channel.json (metadata) and messages.json (the content).
    /// All messages are authored by the package owner.
    /// </summary>
    public class DiscordDataPackageImporter
    {
        private readonly Omniscience service;
        private readonly IngestPipeline pipeline;

        public DiscordDataPackageImporter(Omniscience service, IngestPipeline pipeline)
        {
            this.service = service;
            this.pipeline = pipeline;
        }

        public async Task<int> ImportAsync(string zipPath, CancellationToken ct)
        {
            using var zip = ZipFile.OpenRead(zipPath);

            // Package owner identity from account/user.json.
            var userEntry = FindEntry(zip, "account/user.json")
                ?? throw new InvalidDataException("Not a Discord data package: account/user.json missing.");
            var user = JObject.Parse(ReadEntry(userEntry));
            string ownerId = user.Value<string>("id") ?? throw new InvalidDataException("user.json has no id.");
            string ownerUsername = user.Value<string>("username") ?? ownerId;
            var owner = new HarvestedIdentity
            {
                Platform = "discord",
                PlatformUserId = ownerId,
                Username = ownerUsername,
                DisplayName = user.Value<string>("global_name") ?? ownerUsername,
            };

            int imported = 0;
            var channelDirs = zip.Entries
                .Where(e => e.FullName.Replace('\\', '/').Contains("messages/c") && e.Name == "channel.json")
                .ToList();

            foreach (var channelEntry in channelDirs)
            {
                ct.ThrowIfCancellationRequested();
                JObject channel;
                try { channel = JObject.Parse(ReadEntry(channelEntry)); }
                catch { continue; }

                string channelId = channel.Value<string>("id") ?? "";
                if (channelId.Length == 0) continue;
                int type = channel.Value<int?>("type") ?? 0;
                string kind = type switch { 1 => "dm", 3 => "group_dm", _ => "guild_channel" };
                string? guildId = (channel["guild"] as JObject)?.Value<string>("id");
                string? guildName = (channel["guild"] as JObject)?.Value<string>("name");
                string? title = channel.Value<string>("name");
                // DMs: title from recipients for readability.
                if (kind == "dm" && title == null && channel["recipients"] is JArray recipients)
                    title = string.Join(", ", recipients.Select(r => r.ToString()).Where(r => r != ownerId));

                string dir = channelEntry.FullName[..^"channel.json".Length];
                var messagesEntry = zip.GetEntry(dir + "messages.json");
                if (messagesEntry == null) continue;

                JArray messages;
                try { messages = JArray.Parse(ReadEntry(messagesEntry)); }
                catch { continue; }

                foreach (var m in messages.OfType<JObject>())
                {
                    ct.ThrowIfCancellationRequested();
                    string? messageId = m.Value<string>("ID") ?? m.Value<string>("id");
                    string contents = m.Value<string>("Contents") ?? m.Value<string>("contents") ?? "";
                    string? tsRaw = m.Value<string>("Timestamp") ?? m.Value<string>("timestamp");
                    if (messageId == null || tsRaw == null) continue;
                    if (!DateTime.TryParse(tsRaw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var sentAt))
                        continue;

                    var hm = new HarvestedMessage
                    {
                        Platform = "discord",
                        PlatformMessageId = messageId,
                        SentAt = sentAt,
                        Content = contents.Length > 0 ? contents : null,
                        ConversationKind = kind,
                        GuildId = guildId,
                        GuildName = guildName,
                        ChannelId = channelId,
                        ChannelTitle = title,
                        Author = owner,
                    };
                    string attachments = m.Value<string>("Attachments") ?? "";
                    foreach (var url in attachments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!url.StartsWith("http")) continue;
                        hm.Attachments.Add(new HarvestedAttachment
                        {
                            OriginalUrl = url,
                            Filename = Path.GetFileName(new Uri(url).LocalPath),
                            Kind = "file",
                        });
                    }
                    try
                    {
                        await pipeline.IngestAsync(hm, ct);
                        imported++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Data package message ingest failed"); }
                }
            }
            return imported;
        }

        private static ZipArchiveEntry? FindEntry(ZipArchive zip, string suffix) =>
            zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        private static string ReadEntry(ZipArchiveEntry entry)
        {
            using var s = entry.Open();
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
    }
}
