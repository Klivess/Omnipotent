using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Domain;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Ingest.Discord
{
    /// <summary>
    /// Top-level Discord ingester. Owns one gateway + REST client per active harvest_source.
    /// Handles realtime MESSAGE_CREATE events and runs an opportunistic backfill on startup.
    /// </summary>
    public class DiscordIngester : IPlatformIngester
    {
        public string Platform => DiscordNormaliser.Platform;

        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly IngestPipeline pipeline;
        private readonly HttpClient http;
        private readonly ConcurrentDictionary<string, RunningSource> running = new();
        private CancellationTokenSource? lifetimeCts;

        public event Func<HarvestedMessage, Task>? OnNormalisedMessage;
        public event Func<HarvestedIdentity, Task>? OnIdentityObserved;

        public DiscordIngester(Omniscience service, OmniscienceDb db, IngestPipeline pipeline, HttpClient http)
        {
            this.service = service;
            this.db = db;
            this.pipeline = pipeline;
            this.http = http;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            foreach (var src in LoadActiveSources())
            {
                _ = StartSourceAsync(src, lifetimeCts.Token);
            }
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try { lifetimeCts?.Cancel(); } catch { }
            foreach (var rs in running.Values)
            {
                try { await rs.Gateway.StopAsync(); } catch { }
            }
            running.Clear();
        }

        public Task RequestBackfillAsync(string sourceId, CancellationToken ct)
        {
            if (!running.TryGetValue(sourceId, out var rs))
                throw new InvalidOperationException("Source not running.");
            _ = Task.Run(async () =>
            {
                try
                {
                    var bf = new DiscordHistoryBackfiller(service, db, rs.Rest, pipeline, sourceId);
                    await bf.RunAsync(ct);
                }
                catch (Exception ex) { _ = service.ServiceLogError(ex, "Manual backfill failed"); }
            }, ct);
            return Task.CompletedTask;
        }

        public async Task<(bool ok, string? selfId, string? selfName, string? error)> AddSourceAsync(string token, string? label, CancellationToken ct)
        {
            var rest = new DiscordRestClient(http, token);
            JToken self;
            try { self = await rest.GetSelfAsync(ct); }
            catch (Exception ex) { return (false, null, null, ex.Message); }

            string id = self.Value<string>("id") ?? "";
            string username = self.Value<string>("username") ?? "";
            if (string.IsNullOrEmpty(id)) return (false, null, null, "Token did not yield a user id.");

            string sourceId = Guid.NewGuid().ToString("N");
            byte[] enc = TokenVault.Encrypt(token);
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO harvest_sources
                    (source_id, platform, label, token_encrypted, self_platform_user_id, self_username, status, added_at)
                    VALUES($id,'discord',$lbl,$tok,$sid,$sn,'active',$ts)";
                cmd.Parameters.AddWithValue("$id", sourceId);
                cmd.Parameters.AddWithValue("$lbl", (object?)label ?? username);
                cmd.Parameters.AddWithValue("$tok", enc);
                cmd.Parameters.AddWithValue("$sid", id);
                cmd.Parameters.AddWithValue("$sn", username);
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }

            _ = StartSourceAsync(new HarvestSourceRecord
            {
                SourceId = sourceId,
                Platform = "discord",
                Label = label ?? username,
                TokenEncrypted = enc,
                SelfPlatformUserId = id,
                SelfUsername = username,
                Status = "active",
            }, lifetimeCts?.Token ?? CancellationToken.None);

            return (true, id, username, null);
        }

        public async Task RemoveSourceAsync(string sourceId)
        {
            if (running.TryRemove(sourceId, out var rs))
            {
                try { await rs.Gateway.StopAsync(); } catch { }
            }
            await db.WriteLock.WaitAsync();
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM harvest_sources WHERE source_id=$id";
                cmd.Parameters.AddWithValue("$id", sourceId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        public List<HarvestSourceRecord> LoadActiveSources()
        {
            var list = new List<HarvestSourceRecord>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT source_id, platform, label, token_encrypted, self_platform_user_id, self_username, status, last_status_message, added_at, last_full_sync_at, last_event_at FROM harvest_sources WHERE platform='discord'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HarvestSourceRecord
                {
                    SourceId = r.GetString(0),
                    Platform = r.GetString(1),
                    Label = r.IsDBNull(2) ? null : r.GetString(2),
                    TokenEncrypted = r.IsDBNull(3) ? null : (byte[])r.GetValue(3),
                    SelfPlatformUserId = r.IsDBNull(4) ? null : r.GetString(4),
                    SelfUsername = r.IsDBNull(5) ? null : r.GetString(5),
                    Status = r.GetString(6),
                    LastStatusMessage = r.IsDBNull(7) ? null : r.GetString(7),
                    AddedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)).UtcDateTime,
                    LastFullSyncAt = r.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)).UtcDateTime,
                    LastEventAt = r.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)).UtcDateTime,
                });
            }
            return list;
        }

        private async Task StartSourceAsync(HarvestSourceRecord src, CancellationToken ct)
        {
            string? token = TokenVault.Decrypt(src.TokenEncrypted);
            if (string.IsNullOrEmpty(token))
            {
                await UpdateSourceStatus(src.SourceId, "unauth", "Token could not be decrypted.");
                return;
            }
            var rest = new DiscordRestClient(http, token);
            // Validate quickly.
            try { await rest.GetSelfAsync(ct); }
            catch (Exception ex)
            {
                await UpdateSourceStatus(src.SourceId, "unauth", ex.Message);
                return;
            }

            var gw = new DiscordGateway(token);
            var rs = new RunningSource { Source = src, Rest = rest, Gateway = gw };
            running[src.SourceId] = rs;

            // Live events
            gw.OnDispatch += (eventName, payload) =>
            {
                _ = OnDispatchAsync(src.SourceId, eventName, payload);
            };
            gw.OnError += ex => { _ = service.ServiceLogError(ex, $"Gateway error on source {src.Label}"); };

            try { await gw.StartAsync(ct); }
            catch (Exception ex) { await UpdateSourceStatus(src.SourceId, "failed", ex.Message); return; }

            await UpdateSourceStatus(src.SourceId, "active", null);

            // Kick off backfill in background.
            _ = Task.Run(async () =>
            {
                try
                {
                    var bf = new DiscordHistoryBackfiller(service, db, rest, pipeline, src.SourceId);
                    await bf.RunAsync(ct);
                    await service.ServiceLog($"Discord backfill complete for {src.Label}.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _ = service.ServiceLogError(ex, "Backfill task crashed"); }
            }, ct);
        }

        private async Task OnDispatchAsync(string sourceId, string eventName, JObject payload)
        {
            if (!running.TryGetValue(sourceId, out var rs)) return;
            try
            {
                switch (eventName)
                {
                    case "MESSAGE_CREATE":
                    case "MESSAGE_UPDATE":
                    {
                        string? guildId = payload.Value<string>("guild_id");
                        string? guildName = null;
                        string convKind = guildId != null ? "guild_channel" : "dm";
                        var hm = DiscordNormaliser.MessageFromJson(payload, guildId, guildName, convKind, null);
                        await pipeline.IngestAsync(hm, CancellationToken.None);
                        if (OnNormalisedMessage != null) await OnNormalisedMessage(hm);
                        await UpdateLastEvent(sourceId);
                        break;
                    }
                    case "PRESENCE_UPDATE":
                    {
                        var u = payload["user"] as JObject;
                        if (u != null && OnIdentityObserved != null)
                            await OnIdentityObserved(DiscordNormaliser.IdentityFromJson(u));
                        break;
                    }
                }
            }
            catch (Exception ex) { _ = service.ServiceLogError(ex, $"Failed to handle {eventName}"); }
        }

        private async Task UpdateSourceStatus(string sourceId, string status, string? msg)
        {
            await db.WriteLock.WaitAsync();
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE harvest_sources SET status=$s, last_status_message=$m WHERE source_id=$id";
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$m", (object?)msg ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", sourceId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task UpdateLastEvent(string sourceId)
        {
            await db.WriteLock.WaitAsync();
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE harvest_sources SET last_event_at=$ts WHERE source_id=$id";
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$id", sourceId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private class RunningSource
        {
            public HarvestSourceRecord Source { get; set; } = new();
            public DiscordRestClient Rest { get; set; } = null!;
            public DiscordGateway Gateway { get; set; } = null!;
        }
    }
}
