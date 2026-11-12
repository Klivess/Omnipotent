using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Monitors
{
    public sealed partial class MonitorsModule
    {
        /// <summary>
        /// X46, from what the nightly analytics found: someone who joined Klives's own circle (his community in N12), and
        /// someone new to a community who rose fast there (N13). The first pass only learns what is already the case, so
        /// installing the monitor does not tell Klives about everyone at once.
        /// </summary>
        private async Task<string?> PeopleAsync(JobContext job)
        {
            var ct = job.Cancellation;
            if (!Context.Settings.Get("monitors.new_people.enabled", true)) return Json(new { skipped = "switched off" });
            long now = Context.Clock.NowMs;
            bool first = State("people_initialised") == 0;
            int joined = 0, rising = 0;
            bool hasMembers, hasSpaces;
            string? self, community = null;
            List<string> members = new();
            var newcomers = new List<(string Kind, string Id, string Person, long Recent, string? FirstDay)>();
            using (var corpus = Context.Stores.Corpus.OpenRead())
                self = corpus.Scalar<string>(null, "SELECT COALESCE(pc.canonical_id, p.person_id) FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id WHERE p.is_self=1 ORDER BY p.person_id LIMIT 1");
            using (var c = Context.Stores.Intel.OpenRead())
            {
                hasMembers = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ins_members'") > 0;
                hasSpaces = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ins_spaces'") > 0;
                if (hasMembers && self != null)
                {
                    community = c.Scalar<string>(null, "SELECT community_id FROM ins_members WHERE person_id=$p", ("$p", self));
                    if (community != null) members = c.Query(null, "SELECT person_id FROM ins_members WHERE community_id=$c AND person_id<>$p", r => r.GetString(0), ("$c", community), ("$p", self));
                }
                if (hasSpaces)
                    foreach (var (kind, id, body) in c.Query(null, "SELECT scope_kind, scope_id, body_json FROM ins_spaces", r => (r.GetString(0), r.GetString(1), r.GetString(2))))
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (!doc.RootElement.TryGetProperty("rising", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                        foreach (var item in list.EnumerateArray())
                            if (item.TryGetProperty("isNew", out var n) && n.ValueKind == JsonValueKind.True && item.TryGetProperty("everywhere", out var e) && e.ValueKind == JsonValueKind.False
                                && item.GetProperty("personId").GetString() is { } person)
                                newcomers.Add((kind, id, person, item.GetProperty("recent").GetInt64(), item.TryGetProperty("firstDay", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null));
                    }
            }

            // Klives's circle: who is in it now that was not at the last look.
            HashSet<string> before;
            using (var c = Context.Stores.Intel.OpenRead()) before = c.Query(null, "SELECT person_id FROM mon_circle", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            var arrived = members.Where(m => !before.Contains(m)).ToList();
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM mon_circle");
                foreach (string m in members) c.Exec(tx, "INSERT INTO mon_circle(person_id, community_id, since_ms) VALUES($p,$c,$n)", ("$p", m), ("$c", community), ("$n", now));
            }, ct).ConfigureAwait(false);
            if (!first)
                foreach (string person in arrived)
                    if (await FireAsync($"x46|circle|{person}|{KlivesDay(now)}", "new_person", null, person, null, null, now,
                            new { trigger = "joined_circle", communityId = community, circleSize = members.Count }, NoticeSeverity.Attention, true, ct).ConfigureAwait(false))
                        joined++;

            // Newcomers rising fast in a community: once per person and place.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var c = Context.Stores.Intel.OpenRead())
                seen = c.QueryIn(null, "SELECT seen_key FROM mon_seen WHERE seen_key IN ({keys})", newcomers.Select(x => $"rising|{x.Kind}|{x.Id}|{x.Person}").ToList(), r => r.GetString(0))
                    .ToHashSet(StringComparer.Ordinal);
            foreach (var x in newcomers.OrderByDescending(x => x.Recent).ThenBy(x => x.Person, StringComparer.Ordinal))
            {
                string key = $"rising|{x.Kind}|{x.Id}|{x.Person}";
                if (seen.Contains(key)) continue;
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT OR IGNORE INTO mon_seen(seen_key, person_id, at_ms) VALUES($k,$p,$n)", ("$k", key), ("$p", x.Person), ("$n", now)), ct)
                    .ConfigureAwait(false);
                if (first) continue;
                if (await FireAsync("x46|" + key, "new_person", null, x.Person, x.Kind == "guild" ? x.Id : null, x.Kind == "conversation" ? x.Id : null, now,
                        new { trigger = "rising_newcomer", scopeKind = x.Kind, scopeId = x.Id, recent = x.Recent, firstDay = x.FirstDay }, NoticeSeverity.Info, true, ct).ConfigureAwait(false))
                    rising++;
            }
            if (first) await Context.Stores.Intel.WriteAsync((c, tx) => SetState(c, tx, "people_initialised", 1), ct).ConfigureAwait(false);
            return Json(new { joined, rising, learned = first });
        }
    }
}
