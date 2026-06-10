using DSharpPlus.Entities;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.Omniscience.Domain;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Radar
{
    /// <summary>
    /// Immediate self-mention alerts: the moment anyone mentions Klives — by @mention,
    /// username, or any known alias/nickname — a KliveBot DM fires with the snippet and
    /// context. Alerts are burst-grouped per channel so a conversation about Klives is
    /// one alert, not twenty. v1 aliases come from a setting; M5's alias graph feeds
    /// learned nicknames into <see cref="SetAliases"/> automatically.
    /// </summary>
    public class KlivesRadar
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;

        private volatile List<Regex> aliasRegexes = new();
        private volatile List<string> aliasNames = new();
        private readonly ConcurrentQueue<PendingAlert> pending = new();
        private readonly CancellationTokenSource cts = new();

        // Messages older than this are backfill, not live conversation — never alert.
        private static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(90);

        private class PendingAlert
        {
            public string MatchedAlias = "";
            public string MessageId = "";
            public string AuthorDisplay = "";
            public string ChannelLabel = "";
            public string Snippet = "";
            public DateTime OccurredAt;
            public string? WatchLabel;      // null = built-in Klives radar
            public bool Notify = true;
        }

        private class Watchlist
        {
            public string Label = "";
            public List<Regex> TermRegexes = new();
            public HashSet<string>? AuthorUserIds;  // null = any author
            public bool Notify = true;
        }

        private volatile List<Watchlist> watchlists = new();
        private DateTime lastWatchlistRefresh = DateTime.MinValue;

        public KlivesRadar(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task StartAsync()
        {
            string raw = await service.GetStringOmniSetting("OmniscienceRadarAliases", "klives");
            SetAliases(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            RefreshWatchlists();
            _ = Task.Run(DispatchLoopAsync);
        }

        /// <summary>Reloads watchlists from the DB. Called on a timer and after CRUD.</summary>
        public void RefreshWatchlists()
        {
            try
            {
                var fresh = new List<Watchlist>();
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT label, terms, person_id, notify FROM watchlists WHERE enabled=1";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var w = new Watchlist
                    {
                        Label = r.GetString(0),
                        Notify = r.GetInt32(3) == 1,
                        TermRegexes = r.GetString(1)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(t => t.Length >= 2)
                            .Select(t => new Regex(@"(^|[^a-z0-9_])" + Regex.Escape(t.ToLowerInvariant()) + @"($|[^a-z0-9_])",
                                RegexOptions.IgnoreCase | RegexOptions.Compiled))
                            .ToList(),
                    };
                    if (!r.IsDBNull(2))
                    {
                        // Person-scoped watch: resolve to that person's platform user ids.
                        using var idCmd = conn.CreateCommand();
                        idCmd.CommandText = @"SELECT platform_user_id FROM platform_identities
                            WHERE person_id=$p OR person_id IN (SELECT person_id FROM persons WHERE merged_into_person_id=$p)";
                        idCmd.Parameters.AddWithValue("$p", r.GetString(2));
                        var ids = new HashSet<string>();
                        using var ir = idCmd.ExecuteReader();
                        while (ir.Read()) ids.Add(ir.GetString(0));
                        w.AuthorUserIds = ids;
                    }
                    if (w.TermRegexes.Count > 0) fresh.Add(w);
                }
                watchlists = fresh;
                lastWatchlistRefresh = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, "[Omniscience] Watchlist refresh failed");
            }
        }

        public void Stop() => cts.Cancel();

        private List<string> configuredAliases = new();
        private readonly HashSet<string> learnedAliases = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Replaces the configured alias set (from the OmniSetting).</summary>
        public void SetAliases(IEnumerable<string> aliases)
        {
            configuredAliases = aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
            RebuildRegexes();
        }

        /// <summary>
        /// Merges aliases learned by the alias graph (nicknames people invent for Klives)
        /// into the live set — radar v2: learned, not configured.
        /// </summary>
        public void AddLearnedAliases(IEnumerable<string> aliases)
        {
            lock (learnedAliases)
            {
                foreach (var a in aliases)
                    if (!string.IsNullOrWhiteSpace(a)) learnedAliases.Add(a.Trim());
            }
            RebuildRegexes();
        }

        private void RebuildRegexes()
        {
            List<string> names;
            lock (learnedAliases)
            {
                names = configuredAliases.Concat(learnedAliases)
                    .Where(a => a.Length >= 3)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            aliasNames = names;
            aliasRegexes = names
                .Select(n => new Regex(@"(^|[^a-z0-9_])" + Regex.Escape(n.ToLowerInvariant()) + @"($|[^a-z0-9_])",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled))
                .ToList();
        }

        public IReadOnlyList<string> CurrentAliases => aliasNames;

        /// <summary>Hooked to IngestPipeline.OnMessagePersisted — checks every live message.</summary>
        public void InspectMessage(HarvestedMessage msg)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(msg.Content)) return;
                if (DateTime.UtcNow - msg.SentAt > LiveWindow) return; // backfill
                string klivesId = OmniPaths.KlivesDiscordAccountID.ToString();
                if (msg.Author?.PlatformUserId == klivesId) return;    // own messages

                string channelLabel = msg.ConversationKind switch
                {
                    "dm" => "DM",
                    "group_dm" => "Group DM" + (msg.ChannelTitle != null ? $" '{msg.ChannelTitle}'" : ""),
                    _ => $"{msg.GuildName ?? msg.GuildId ?? "server"}#{msg.ChannelTitle ?? msg.ChannelId}",
                };
                string snippet = msg.Content.Length > 300 ? msg.Content[..300] + "…" : msg.Content;
                void Enqueue(string matched, string? watchLabel, bool notify) => pending.Enqueue(new PendingAlert
                {
                    MatchedAlias = matched,
                    MessageId = msg.Platform + ":" + msg.PlatformMessageId,
                    AuthorDisplay = msg.Author?.DisplayName ?? msg.Author?.Username ?? "unknown",
                    ChannelLabel = channelLabel,
                    Snippet = snippet,
                    OccurredAt = msg.SentAt,
                    WatchLabel = watchLabel,
                    Notify = notify,
                });

                // Built-in Klives radar.
                string? matched = null;
                if (msg.Content.Contains("<@" + klivesId + ">") || msg.Content.Contains("<@!" + klivesId + ">"))
                    matched = "@mention";
                else
                {
                    var regexes = aliasRegexes;
                    var names = aliasNames;
                    for (int i = 0; i < regexes.Count; i++)
                    {
                        if (!regexes[i].IsMatch(msg.Content)) continue;
                        matched = names[i];
                        break;
                    }
                }
                if (matched != null) Enqueue(matched, null, true);

                // Generic watchlists (any keyword/topic, optional person scope).
                if (DateTime.UtcNow - lastWatchlistRefresh > TimeSpan.FromMinutes(5)) RefreshWatchlists();
                foreach (var w in watchlists)
                {
                    if (w.AuthorUserIds != null && (msg.Author?.PlatformUserId == null || !w.AuthorUserIds.Contains(msg.Author.PlatformUserId)))
                        continue;
                    foreach (var re in w.TermRegexes)
                    {
                        if (!re.IsMatch(msg.Content)) continue;
                        Enqueue("watch:" + w.Label, w.Label, w.Notify);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, "[Omniscience] Radar inspection failed");
            }
        }

        // Groups pending alerts per channel within the burst window, then DMs Klives.
        private async Task DispatchLoopAsync()
        {
            var held = new List<PendingAlert>();
            DateTime? oldestHeld = null;
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), cts.Token); }
                catch (OperationCanceledException) { break; }

                while (pending.TryDequeue(out var a))
                {
                    held.Add(a);
                    oldestHeld ??= DateTime.UtcNow;
                }
                if (held.Count == 0) continue;
                // Hold briefly so a burst of messages about Klives becomes one alert.
                if (DateTime.UtcNow - oldestHeld < BurstWindow && held.Count < 10) continue;

                var batch = held.ToList();
                held.Clear();
                oldestHeld = null;
                try { await SendGroupedAlerts(batch); }
                catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Radar alert dispatch failed"); }
            }
        }

        private async Task SendGroupedAlerts(List<PendingAlert> alerts)
        {
            var bots = await service.GetServicesByType<KliveBotDiscord>();
            KliveBotDiscord? bot = bots is { Length: > 0 } ? bots[0] as KliveBotDiscord : null;

            foreach (var group in alerts.GroupBy(a => (a.WatchLabel, a.ChannelLabel)))
            {
                bool wantsDm = group.Any(a => a.Notify);
                var sb = new StringBuilder();
                foreach (var a in group.OrderBy(a => a.OccurredAt).Take(8))
                    sb.AppendLine($"**{a.AuthorDisplay}**: {a.Snippet}");
                if (group.Count() > 8) sb.AppendLine($"…and {group.Count() - 8} more.");
                string aliasesHit = string.Join(", ", group.Select(a => a.MatchedAlias).Distinct());

                bool sent = false;
                if (bot != null && wantsDm)
                {
                    try
                    {
                        string title = group.Key.WatchLabel == null
                            ? $"📡 Radar: you came up in {group.Key.ChannelLabel}"
                            : $"👁 Watchlist '{group.Key.WatchLabel}' hit in {group.Key.ChannelLabel}";
                        var embed = KliveBotDiscord.MakeSimpleEmbed(title,
                            $"Matched: {aliasesHit}\n\n{sb}",
                            group.Key.WatchLabel == null ? DiscordColor.Purple : DiscordColor.Orange);
                        await bot.SendMessageToKlives(embed);
                        sent = true;
                    }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Radar DM failed"); }
                }

                await PersistAlerts(group.ToList(), notified: sent);
            }
        }

        private async Task PersistAlerts(List<PendingAlert> alerts, bool notified)
        {
            await db.WriteLock.WaitAsync();
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                foreach (var a in alerts)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO radar_alerts(matched_alias, message_id, author_display, channel_label, snippet, occurred_at, notified, watch_label)
                        VALUES($al,$m,$au,$c,$s,$t,$n,$w)";
                    cmd.Parameters.AddWithValue("$al", a.MatchedAlias);
                    cmd.Parameters.AddWithValue("$m", a.MessageId);
                    cmd.Parameters.AddWithValue("$au", a.AuthorDisplay);
                    cmd.Parameters.AddWithValue("$c", a.ChannelLabel);
                    cmd.Parameters.AddWithValue("$s", a.Snippet);
                    cmd.Parameters.AddWithValue("$t", new DateTimeOffset(a.OccurredAt).ToUnixTimeMilliseconds());
                    cmd.Parameters.AddWithValue("$n", notified ? 1 : 0);
                    cmd.Parameters.AddWithValue("$w", (object?)a.WatchLabel ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
        }
    }
}
