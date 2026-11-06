using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    /// <summary>One validity interval of a name (or avatar hash) of an identity.</summary>
    public sealed record NameInterval(string Kind, string Value, long ValidFromMs, long? ValidToMs);

    /// <summary>
    /// Turns v2's identity history (old → new pairs with a change time, plus the current value) into validity
    /// intervals, so "what was their name in March" has an answer (plan T5).
    /// </summary>
    public static class NameTimeline
    {
        public static IReadOnlyList<NameInterval> Build(string kind, long firstSeenMs, string? current, IEnumerable<(long AtMs, string? Old, string? New)> changes)
        {
            var ordered = changes.OrderBy(c => c.AtMs).ToList();
            var result = new List<NameInterval>();
            long start = ordered.Count > 0 ? Math.Min(firstSeenMs, ordered[0].AtMs) : firstSeenMs;
            string? value = ordered.Count > 0 ? ordered[0].Old : current;
            foreach (var ch in ordered)
            {
                // Zero-length intervals (a change at the very instant the identity was first seen) are dropped.
                if (!string.IsNullOrEmpty(value) && ch.AtMs > start) result.Add(new NameInterval(kind, value, start, ch.AtMs));
                start = ch.AtMs;
                value = ch.New;
            }
            string? last = !string.IsNullOrEmpty(current) ? current : value;
            if (!string.IsNullOrEmpty(last)) result.Add(new NameInterval(kind, last, start, null));
            // Two changes in the same millisecond: the later one wins that instant.
            return result.GroupBy(i => i.ValidFromMs).Select(g => g.Last()).OrderBy(i => i.ValidFromMs).ToList();
        }
    }

    public sealed partial class LegacyImporter
    {
        private static readonly Dictionary<string, string> HistoryFieldToKind = new(StringComparer.Ordinal)
        {
            ["username"] = "username",
            ["display_name"] = "display",
            ["bio"] = "bio",
            ["custom_status"] = "custom_status",
        };

        // ── sources ──────────────────────────────────────────────────────────────

        private sealed record LegacySource(string Id, string Platform, string? Label, byte[]? Token, string? SelfUserId, string? Status,
            string? StatusMessage, object? AddedAt, object? LastEventAt);

        private async Task ImportSourcesAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, CancellationToken ct)
        {
            const string table = "harvest_sources";
            var rows = ReadAll(legacy, table,
                $"source_id, platform, {legacy.ColumnOrNull(table, "label")}, {legacy.ColumnOrNull(table, "token_encrypted")}, " +
                $"{legacy.ColumnOrNull(table, "self_platform_user_id")}, {legacy.ColumnOrNull(table, "status")}, " +
                $"{legacy.ColumnOrNull(table, "last_status_message")}, {legacy.ColumnOrNull(table, "added_at")}, {legacy.ColumnOrNull(table, "last_event_at")}",
                // Ordinal 0 is the rowid ReadAll prepends.
                r => new LegacySource(r.Text(1) ?? "", r.Text(2) ?? "", r.Text(3), r.IsDBNull(4) ? null : r.GetValue(4) as byte[],
                    r.Text(5), r.Text(6), r.Text(7), r.Raw(8), r.Raw(9)));

            // Secrets are probed outside the writer; only the verdict is kept.
            var probes = rows.ToDictionary(x => x.Row.Id, x => LegacySecrets.Probe(x.Row.Token), StringComparer.Ordinal);
            foreach (var (_, s) in rows)
            {
                if (!string.IsNullOrWhiteSpace(s.SelfUserId) && !string.IsNullOrWhiteSpace(s.Platform))
                    ctx.SelfIdentities.Add(CorpusIds.Identity(s.Platform, s.SelfUserId));
                string verdict = probes[s.Id];
                if (verdict is LegacySecrets.NotDecryptable or LegacySecrets.Unavailable)
                    report.Notes.Add($"Source {s.Id} ({s.Platform}): its stored token is {verdict} on this machine; re-add it before v3 uses this source.");
            }

            var tr = report.Table(table);
            long now = clock.NowMs;
            var counts = await stores.Corpus.WriteAsync((c, tx) =>
            {
                var bc = new BatchCounts();
                // The synthetic source every migrated row is attributed to (v2 did not record which account captured what).
                int added = c.Exec(tx, """
                    INSERT OR IGNORE INTO sources(source_id, platform, kind, label, status, rank, created_at_ms, updated_at_ms)
                    VALUES($id,'omniscience','legacy','Omniscience v2 migration','active',0,$now,$now)
                    """, ("$id", CorpusIds.LegacySourceId), ("$now", now));
                if (added == 1) bc += new BatchCounts(1, 0, 0, 0);
                foreach (var (rowid, s) in rows)
                {
                    if (string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Platform))
                    {
                        if (Quarantine(c, tx, table, rowid.ToString(), "missing source id or platform", new { s.Id, s.Platform, s.Label }, now)) bc += new BatchCounts(0, 0, 0, 1);
                        continue;
                    }
                    long created = Ms(s.AddedAt, now);
                    long updated = Ms(s.LastEventAt, created);
                    var existing = c.Query(tx, "SELECT label, self_platform_user_id, status, status_detail, secret_blob FROM sources WHERE source_id=$id",
                        r => (Label: r.GetStringOrNull(0), Self: r.GetStringOrNull(1), Status: r.GetStringOrNull(2), Detail: r.GetStringOrNull(3),
                              Blob: r.IsDBNull(4) ? null : (byte[])r.GetValue(4)), ("$id", s.Id)).FirstOrDefault();
                    string status = s.Status ?? "unknown";
                    if (existing == default)
                    {
                        c.Exec(tx, """
                            INSERT INTO sources(source_id, platform, kind, label, self_platform_user_id, secret_blob, status, status_detail, rank, created_at_ms, updated_at_ms)
                            VALUES($id,$p,$k,$l,$self,$blob,$st,$sd,10,$c,$u)
                            """, ("$id", s.Id), ("$p", s.Platform.ToLowerInvariant()), ("$k", s.Platform.ToLowerInvariant() + "-account"), ("$l", s.Label),
                            ("$self", s.SelfUserId), ("$blob", s.Token), ("$st", status), ("$sd", s.StatusMessage), ("$c", created), ("$u", updated));
                        bc += new BatchCounts(1, 0, 0, 0);
                    }
                    else if (existing.Label != s.Label || existing.Self != s.SelfUserId || existing.Status != status || existing.Detail != s.StatusMessage
                             || !(existing.Blob ?? Array.Empty<byte>()).AsSpan().SequenceEqual(s.Token ?? Array.Empty<byte>()))
                    {
                        c.Exec(tx, """
                            UPDATE sources SET label=$l, self_platform_user_id=$self, secret_blob=$blob, status=$st, status_detail=$sd, updated_at_ms=$u
                            WHERE source_id=$id
                            """, ("$id", s.Id), ("$l", s.Label), ("$self", s.SelfUserId), ("$blob", s.Token), ("$st", status), ("$sd", s.StatusMessage), ("$u", Math.Max(updated, now)));
                        bc += new BatchCounts(0, 1, 0, 0);
                    }
                    else bc += new BatchCounts(0, 0, 1, 0);
                }
                return bc;
            }, ct).ConfigureAwait(false);
            tr.Add(counts, rows.Count);
        }

        // ── persons, merges, canonical map ───────────────────────────────────────

        private sealed record LegacyPerson(string Id, string? DisplayName, string? Notes, object? CreatedAt, object? UpdatedAt, string? MergedInto, string? Tier);

        private async Task ImportPersonsAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, CancellationToken ct)
        {
            const string table = "persons";
            var rows = ReadAll(legacy, table,
                $"person_id, {legacy.ColumnOrNull(table, "display_name")}, {legacy.ColumnOrNull(table, "notes")}, {legacy.ColumnOrNull(table, "created_at")}, " +
                $"{legacy.ColumnOrNull(table, "updated_at")}, {legacy.ColumnOrNull(table, "merged_into_person_id")}, {legacy.ColumnOrNull(table, "tier")}",
                r => new LegacyPerson(r.Text(1) ?? "", r.Text(2), r.Text(3), r.Raw(4), r.Raw(5), NullIfBlank(r.Text(6)), r.Text(7)));

            var valid = rows.Where(x => !string.IsNullOrWhiteSpace(x.Row.Id)).ToList();
            var chains = MergeChains.Resolve(valid.ToDictionary(x => x.Row.Id, x => x.Row.MergedInto == x.Row.Id ? null : x.Row.MergedInto, StringComparer.Ordinal));
            foreach (var (id, root) in chains.Canonical) ctx.Canonical[id] = root;
            foreach (var cycle in chains.Cycles) report.Notes.Add($"Merge cycle among persons {string.Join(" → ", cycle)}; resolved to {cycle.Min(StringComparer.Ordinal)}.");
            foreach (var d in chains.DanglingLinks) report.Notes.Add($"Person {d} was merged into a person that does not exist; kept as its own person.");
            ctx.Persons.Clear();
            foreach (var (_, p) in valid) ctx.Persons[p.Id] = p;

            var tr = report.Table(table);
            long now = clock.NowMs;
            var counts = await stores.Corpus.WriteAsync((c, tx) =>
            {
                var bc = new BatchCounts();
                var existing = c.Query(tx, "SELECT person_id, display_name FROM persons", r => (r.GetString(0), r.GetStringOrNull(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                var openMerges = c.Query(tx, "SELECT from_person_id, into_person_id FROM person_merges WHERE undone_at_ms IS NULL",
                        r => (r.GetString(0), r.GetString(1)))
                    .GroupBy(x => x.Item1, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Item2, StringComparer.Ordinal);
                var canonicalNow = c.Query(tx, "SELECT person_id, canonical_id FROM person_canonical", r => (r.GetString(0), r.GetString(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

                using var insertPerson = new PreparedStatement(c, tx,
                    "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($id,$c,$n,0)", "$id", "$c", "$n");
                foreach (var (rowid, p) in rows)
                {
                    if (string.IsNullOrWhiteSpace(p.Id))
                    {
                        if (Quarantine(c, tx, table, rowid.ToString(), "person without an id", p, now)) bc += new BatchCounts(0, 0, 0, 1);
                        continue;
                    }
                    string? name = NullIfBlank(p.DisplayName);
                    if (!existing.TryGetValue(p.Id, out var oldName))
                    {
                        insertPerson.Run(p.Id, Ms(p.CreatedAt, now), name);
                        Change(c, tx, now, ChangeKinds.PersonCreate, p.Id, p.Id, null);
                        bc += new BatchCounts(1, 0, 0, 0);
                    }
                    else if (oldName != name)
                    {
                        c.Exec(tx, "UPDATE persons SET display_name=$n WHERE person_id=$id", ("$n", name), ("$id", p.Id));
                        Change(c, tx, now, ChangeKinds.PersonCreate, p.Id, p.Id, null);
                        bc += new BatchCounts(0, 1, 0, 0);
                    }
                    else bc += new BatchCounts(0, 0, 1, 0);

                    // Merge record: v2's direct link (not the resolved root) is what Klives decided.
                    string? target = p.MergedInto == p.Id ? null : p.MergedInto;
                    if (target != null && !chains.DanglingLinks.Contains(p.Id))
                    {
                        if (!openMerges.TryGetValue(p.Id, out var currentTarget) || currentTarget != target)
                        {
                            if (currentTarget != null)
                                c.Exec(tx, "UPDATE person_merges SET undone_at_ms=$n WHERE from_person_id=$f AND undone_at_ms IS NULL", ("$n", now), ("$f", p.Id));
                            c.Exec(tx, "INSERT INTO person_merges(from_person_id, into_person_id, merged_at_ms, actor, reason) VALUES($f,$i,$t,'klives (v2)','merged in Omniscience v2')",
                                ("$f", p.Id), ("$i", target), ("$t", Ms(p.UpdatedAt, now)));
                        }
                    }
                    else if (openMerges.ContainsKey(p.Id))
                    {
                        c.Exec(tx, "UPDATE person_merges SET undone_at_ms=$n WHERE from_person_id=$f AND undone_at_ms IS NULL", ("$n", now), ("$f", p.Id));
                    }

                    string root = ctx.CanonicalOf(p.Id)!;
                    if (!canonicalNow.TryGetValue(p.Id, out var currentRoot) || currentRoot != root)
                    {
                        c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$c) ON CONFLICT(person_id) DO UPDATE SET canonical_id=excluded.canonical_id",
                            ("$p", p.Id), ("$c", root));
                        if (currentRoot != null || root != p.Id) Change(c, tx, now, ChangeKinds.PersonMerge, p.Id, root, null);
                    }
                }
                return bc;
            }, ct).ConfigureAwait(false);
            tr.Add(counts, rows.Count);
        }

        // ── identities and their names ───────────────────────────────────────────

        private sealed record LegacyIdentity(string Id, string? PersonId, string? Platform, string? PlatformUserId, string? Username, string? DisplayName,
            string? Bio, object? FirstSeen, object? LastSeen);

        private sealed record LegacyHistory(long Rowid, string IdentityId, string Field, string? Old, string? New, long AtMs);

        private async Task ImportIdentitiesAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            const string table = "platform_identities";
            var rows = ReadAll(legacy, table,
                $"identity_id, person_id, platform, platform_user_id, {legacy.ColumnOrNull(table, "platform_username")}, {legacy.ColumnOrNull(table, "display_name")}, " +
                $"{legacy.ColumnOrNull(table, "bio")}, {legacy.ColumnOrNull(table, "first_seen")}, {legacy.ColumnOrNull(table, "last_seen")}",
                r => new LegacyIdentity(r.Text(1) ?? "", r.Text(2), r.Text(3), r.Text(4), r.Text(5), r.Text(6), r.Text(7), r.Raw(8), r.Raw(9)));

            foreach (var (_, i) in rows)
            {
                if (string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.Platform) || string.IsNullOrWhiteSpace(i.PlatformUserId) || string.IsNullOrWhiteSpace(i.PersonId)) continue;
                string v3Id = CorpusIds.Identity(i.Platform, i.PlatformUserId);
                ctx.Identities[i.Id] = (v3Id, i.PersonId, i.Platform.Trim().ToLowerInvariant());
                ctx.PersonByIdentity[v3Id] = i.PersonId;
            }

            // Identity history after the cursor (all of it on the first run), read outside the writer.
            const string historyTable = "identity_history";
            long historyCursor = Cursor(historyTable);
            var history = legacy.HasTable(historyTable)
                ? legacy.Connection.Query(null, "SELECT rowid, identity_id, field, old_value, new_value, changed_at FROM identity_history WHERE rowid > $c ORDER BY rowid",
                        r => new LegacyHistory(r.GetInt64(0), r.Text(1) ?? "", r.Text(2) ?? "", r.Text(3), r.Text(4), Ms(r.Raw(5), 0)), ("$c", historyCursor))
                : new List<LegacyHistory>();
            var historyByIdentity = history.GroupBy(h => h.IdentityId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            long historyLast = history.Count == 0 ? historyCursor : history[^1].Rowid;

            var altNames = legacy.HasTable("identity_alt_names")
                ? legacy.Connection.Query(null, $"SELECT identity_id, alt_name, {legacy.ColumnOrNull("identity_alt_names", "source")}, {legacy.ColumnOrNull("identity_alt_names", "first_seen")} FROM identity_alt_names",
                    r => (Identity: r.Text(0) ?? "", Name: r.Text(1), Source: r.Text(2), FirstSeen: r.Raw(3)))
                : new List<(string Identity, string? Name, string? Source, object? FirstSeen)>();

            var tr = report.Table(table);
            var htr = report.Table(historyTable);
            long now = clock.NowMs;
            var counts = await stores.Corpus.WriteAsync((c, tx) =>
            {
                var bc = new BatchCounts();
                var existing = c.Query(tx, "SELECT identity_id, person_id, first_seen_ms, last_seen_ms FROM identities",
                        r => (Id: r.GetString(0), Person: r.GetString(1), First: r.GetInt64(2), Last: r.GetInt64(3)))
                    .ToDictionary(x => x.Id, StringComparer.Ordinal);
                using var insertIdentity = new PreparedStatement(c, tx,
                    "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($id,$p,$u,$person,'user',$f,$l)",
                    "$id", "$p", "$u", "$person", "$f", "$l");
                using var insertName = new PreparedStatement(c, tx,
                    "INSERT OR IGNORE INTO identity_names(identity_id, name_kind, value, valid_from_ms, valid_to_ms, source_kind) VALUES($id,$k,$v,$f,$t,'legacy')",
                    "$id", "$k", "$v", "$f", "$t");
                using var insertAvatar = new PreparedStatement(c, tx,
                    "INSERT OR IGNORE INTO identity_avatars(identity_id, avatar_hash, valid_from_ms, valid_to_ms) VALUES($id,$h,$f,$t)",
                    "$id", "$h", "$f", "$t");

                foreach (var (rowid, i) in rows)
                {
                    if (string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.Platform) || string.IsNullOrWhiteSpace(i.PlatformUserId) || string.IsNullOrWhiteSpace(i.PersonId))
                    {
                        if (Quarantine(c, tx, table, rowid.ToString(), "identity without platform, user id or person", i, now)) bc += new BatchCounts(0, 0, 0, 1);
                        continue;
                    }
                    var (v3Id, personId, platform) = ctx.Identities[i.Id];
                    long first = Ms(i.FirstSeen, now);
                    long last = Math.Max(first, Ms(i.LastSeen, first));
                    string? canonical = ctx.CanonicalOf(personId);
                    historyByIdentity.TryGetValue(i.Id, out var changes);
                    changes ??= new List<LegacyHistory>();

                    if (!existing.TryGetValue(v3Id, out var cur))
                    {
                        insertIdentity.Run(v3Id, platform, i.PlatformUserId!.Trim(), personId, first, last);
                        foreach (var (kind, current) in new[] { ("username", i.Username), ("display", i.DisplayName), ("bio", i.Bio) })
                        {
                            string field = HistoryFieldToKind.First(kv => kv.Value == kind).Key;
                            foreach (var n in NameTimeline.Build(kind, first, NullIfBlank(current),
                                         changes.Where(h => h.Field == field).Select(h => (h.AtMs, NullIfBlank(h.Old), NullIfBlank(h.New)))))
                                insertName.Run(v3Id, n.Kind, n.Value, n.ValidFromMs, n.ValidToMs);
                        }
                        foreach (var n in NameTimeline.Build("custom_status", first, null,
                                     changes.Where(h => h.Field == "custom_status").Select(h => (h.AtMs, NullIfBlank(h.Old), NullIfBlank(h.New)))))
                            insertName.Run(v3Id, n.Kind, n.Value, n.ValidFromMs, n.ValidToMs);
                        foreach (var a in NameTimeline.Build("avatar", first, null,
                                     changes.Where(h => h.Field == "avatar").Select(h => (h.AtMs, NullIfBlank(h.Old), NullIfBlank(h.New)))))
                            insertAvatar.Run(v3Id, a.Value, a.ValidFromMs, a.ValidToMs);
                        Change(c, tx, now, ChangeKinds.IdentityUpsert, v3Id, canonical, null);
                        bc += new BatchCounts(1, 0, 0, 0);
                        continue;
                    }

                    bool changed = false;
                    if (cur.Person != personId || last > cur.Last || first < cur.First)
                    {
                        c.Exec(tx, "UPDATE identities SET person_id=$p, first_seen_ms=MIN(first_seen_ms,$f), last_seen_ms=MAX(last_seen_ms,$l) WHERE identity_id=$id",
                            ("$p", personId), ("$f", first), ("$l", last), ("$id", v3Id));
                        changed = cur.Person != personId;
                    }
                    // New history rows for a known identity: close the open interval and open the new value.
                    foreach (var h in changes.OrderBy(h => h.AtMs).ThenBy(h => h.Rowid))
                    {
                        if (h.Field == "avatar")
                        {
                            c.Exec(tx, "UPDATE identity_avatars SET valid_to_ms=$t WHERE identity_id=$id AND valid_to_ms IS NULL AND valid_from_ms < $t", ("$t", h.AtMs), ("$id", v3Id));
                            if (NullIfBlank(h.New) is { } hash) insertAvatar.Run(v3Id, hash, h.AtMs, null);
                            changed = true;
                            continue;
                        }
                        if (!HistoryFieldToKind.TryGetValue(h.Field, out var kind)) continue;
                        c.Exec(tx, "UPDATE identity_names SET valid_to_ms=$t WHERE identity_id=$id AND name_kind=$k AND valid_to_ms IS NULL AND valid_from_ms < $t",
                            ("$t", h.AtMs), ("$id", v3Id), ("$k", kind));
                        if (NullIfBlank(h.New) is { } value) insertName.Run(v3Id, kind, value, h.AtMs, null);
                        changed = true;
                    }
                    // A value v2 learned without logging a change (it only logs changes from a non-empty value).
                    foreach (var (kind, current) in new[] { ("username", i.Username), ("display", i.DisplayName), ("bio", i.Bio) })
                    {
                        if (NullIfBlank(current) is not { } value) continue;
                        bool hasOpen = c.Scalar<long>(tx, "SELECT COUNT(*) FROM identity_names WHERE identity_id=$id AND name_kind=$k AND valid_to_ms IS NULL",
                            ("$id", v3Id), ("$k", kind)) > 0;
                        if (!hasOpen) { insertName.Run(v3Id, kind, value, first, null); changed = true; }
                    }
                    if (changed)
                    {
                        Change(c, tx, now, ChangeKinds.IdentityName, v3Id, canonical, null);
                        bc += new BatchCounts(0, 1, 0, 0);
                    }
                    else bc += new BatchCounts(0, 0, 1, 0);
                }

                // Alternative names (per-guild nicknames, global names): kept as open intervals from when first seen.
                foreach (var a in altNames)
                {
                    if (!ctx.Identities.TryGetValue(a.Identity, out var ident) || NullIfBlank(a.Name) is not { } alt) continue;
                    string kind = "alt:" + (NullIfBlank(a.Source) ?? "unknown");
                    long from = Ms(a.FirstSeen, existing.TryGetValue(ident.IdentityId, out var e) ? e.First : now);
                    insertName.Run(ident.IdentityId, kind, alt, from, null);
                }

                // The accounts v2's sources logged in as are Klives himself.
                foreach (var self in ctx.SelfIdentities)
                {
                    var person = c.Scalar<string>(tx, "SELECT person_id FROM identities WHERE identity_id=$id", ("$id", self));
                    if (person == null) continue;
                    string root = ctx.CanonicalOf(person)!;
                    c.Exec(tx, "UPDATE persons SET is_self=1 WHERE person_id IN ($p,$r) AND is_self=0", ("$p", person), ("$r", root));
                }

                SaveCursor(c, tx, historyTable, historyLast, history.Count, 0, now);
                return bc;
            }, ct).ConfigureAwait(false);
            tr.Add(counts, rows.Count);
            htr.Read += history.Count;
            htr.Imported += history.Count;
            if (progress != null) await progress(new LegacyProgress(table, rows.Count, rows.Count)).ConfigureAwait(false);
        }
    }
}
