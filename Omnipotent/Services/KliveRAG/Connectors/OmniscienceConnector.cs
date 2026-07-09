using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Connectors
{
    /// <summary>
    /// Indexes Omniscience's <em>distilled</em> knowledge — person facts (grouped per person+category),
    /// Q&amp;A pairs, hypotheses and the latest personality profiles — so agents can draw on what
    /// Omniscience has concluded about people. Raw messages are deliberately NOT copied here; the
    /// query-time federation leg (<see cref="OmniscienceFederation"/>) reaches those through
    /// Omniscience's own message-embedding index instead of re-embedding millions of rows.
    /// Read-only against omniscience.db; per-table watermarks keep passes incremental.
    /// </summary>
    public sealed class OmniscienceConnector : RagConnector
    {
        public override string Name => RagSource.Omniscience;

        private readonly Func<Task<Omniscience.Omniscience?>> resolveOmniscience;

        public OmniscienceConnector(RagIndexWriter writer, Action<string> log, Func<Task<Omniscience.Omniscience?>> resolveOmniscience)
            : base(writer, log)
        {
            this.resolveOmniscience = resolveOmniscience;
        }

        public override async Task RunIncrementalAsync(CancellationToken ct)
        {
            var omni = await resolveOmniscience();
            if (omni?.Db == null) return;

            try
            {
                using var conn = omni.Db.Open();
                await IndexFactsAsync(conn, ct);
                await IndexQaAsync(conn, ct);
                await IndexHypothesesAsync(conn, ct);
                await IndexProfilesAsync(conn, ct);
            }
            catch (Exception ex) { Log($"[KliveRAG] omniscience connector failed: {ex.Message}"); }
        }

        private async Task IndexFactsAsync(SqliteConnection conn, CancellationToken ct)
        {
            const string cursor = "omniscience:facts";
            long watermark = long.TryParse(GetCursor(cursor), out var w) ? w : 0;

            // Which person+category groups have a newer fact than we last saw?
            var groups = new List<(string PersonId, string Category)>();
            long maxSeen = watermark;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT person_id, category, MAX(updated_at) mx FROM person_facts
                                    WHERE status='active' GROUP BY person_id, category HAVING mx > $wm";
                cmd.Parameters.AddWithValue("$wm", watermark);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    groups.Add((r.GetString(0), r.GetString(1)));
                    maxSeen = Math.Max(maxSeen, r.GetInt64(2));
                }
            }

            foreach (var (personId, category) in groups)
            {
                ct.ThrowIfCancellationRequested();
                var facts = new List<string>();
                long created = 0;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT fact_text, updated_at FROM person_facts
                                        WHERE person_id=$p AND category=$c AND status='active' ORDER BY confidence DESC";
                    cmd.Parameters.AddWithValue("$p", personId);
                    cmd.Parameters.AddWithValue("$c", category);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) { facts.Add(r.GetString(0)); created = Math.Max(created, r.IsDBNull(1) ? 0 : r.GetInt64(1)); }
                }
                if (facts.Count == 0) continue;

                string name = PersonName(conn, personId);
                string text = $"{name} — {category}:\n" + string.Join("\n", facts.Select(f => "• " + f));
                var doc = new RagDocument
                {
                    DocId = $"omnifact:{personId}:{category}",
                    Source = RagSource.Omniscience,
                    Title = $"{name}: {category}",
                    Content = text,
                    ContentHash = RagChunker.Hash(text),
                    CreatedAtUnixMs = created > 0 ? created : RagTime.Now,
                    SingleChunk = true,
                };
                await Writer.UpsertAsync(doc, ct);
            }

            if (maxSeen > watermark) await SetCursorAsync(cursor, maxSeen.ToString(), ct);
        }

        private async Task IndexQaAsync(SqliteConnection conn, CancellationToken ct)
        {
            const string cursor = "omniscience:qa";
            long watermark = long.TryParse(GetCursor(cursor), out var w) ? w : 0;
            long maxSeen = watermark;

            var rows = new List<(long Id, string Q, string A, string? Cat, long Created)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT qa_id, question, answer, category, COALESCE(occurred_at, extracted_at), extracted_at
                                    FROM qa_pairs WHERE extracted_at > $wm ORDER BY extracted_at LIMIT 5000";
                cmd.Parameters.AddWithValue("$wm", watermark);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                              r.IsDBNull(4) ? RagTime.Now : r.GetInt64(4)));
                    maxSeen = Math.Max(maxSeen, r.GetInt64(5));
                }
            }

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                string text = $"Q: {row.Q}\nA: {row.A}";
                var doc = new RagDocument
                {
                    DocId = $"omniqa:{row.Id}",
                    Source = RagSource.Omniscience,
                    Title = string.IsNullOrEmpty(row.Cat) ? "Q&A" : $"Q&A · {row.Cat}",
                    Content = text,
                    ContentHash = RagChunker.Hash(text),
                    CreatedAtUnixMs = row.Created,
                    SingleChunk = true,
                };
                await Writer.UpsertAsync(doc, ct);
            }

            if (maxSeen > watermark) await SetCursorAsync(cursor, maxSeen.ToString(), ct);
        }

        private async Task IndexHypothesesAsync(SqliteConnection conn, CancellationToken ct)
        {
            const string cursor = "omniscience:hypotheses";
            long watermark = long.TryParse(GetCursor(cursor), out var w) ? w : 0;
            long maxSeen = watermark;

            var rows = new List<(string Id, string PersonId, string Statement, string? Rationale, string Status, long Created)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT hypothesis_id, person_id, statement, rationale, status, created_at
                                    FROM hypotheses WHERE created_at > $wm ORDER BY created_at LIMIT 5000";
                cmd.Parameters.AddWithValue("$wm", watermark);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                              r.GetString(4), r.GetInt64(5)));
                    maxSeen = Math.Max(maxSeen, r.GetInt64(5));
                }
            }

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                string name = PersonName(conn, row.PersonId);
                string text = $"Hypothesis about {name} [{row.Status}]: {row.Statement}"
                    + (string.IsNullOrWhiteSpace(row.Rationale) ? "" : $"\nRationale: {row.Rationale}");
                var doc = new RagDocument
                {
                    DocId = $"omnihyp:{row.Id}",
                    Source = RagSource.Omniscience,
                    Title = $"Hypothesis · {name}",
                    Content = text,
                    ContentHash = RagChunker.Hash(text),
                    CreatedAtUnixMs = row.Created,
                    SingleChunk = true,
                };
                await Writer.UpsertAsync(doc, ct);
            }

            if (maxSeen > watermark) await SetCursorAsync(cursor, maxSeen.ToString(), ct);
        }

        private async Task IndexProfilesAsync(SqliteConnection conn, CancellationToken ct)
        {
            const string cursor = "omniscience:profiles";
            long watermark = long.TryParse(GetCursor(cursor), out var w) ? w : 0;
            long maxSeen = watermark;

            // Latest profile per person that's newer than the watermark.
            var rows = new List<(string PersonId, string Markdown, long Generated)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT p.person_id, p.profile_markdown, p.generated_at
                    FROM personality_profiles p
                    JOIN (SELECT person_id, MAX(generated_at) mx FROM personality_profiles GROUP BY person_id) latest
                      ON latest.person_id = p.person_id AND latest.mx = p.generated_at
                    WHERE p.generated_at > $wm AND p.profile_markdown IS NOT NULL";
                cmd.Parameters.AddWithValue("$wm", watermark);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(1)) continue;
                    rows.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
                    maxSeen = Math.Max(maxSeen, r.GetInt64(2));
                }
            }

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(row.Markdown)) continue;
                string name = PersonName(conn, row.PersonId);
                var doc = new RagDocument
                {
                    DocId = $"omniprofile:{row.PersonId}",
                    Source = RagSource.Omniscience,
                    Title = $"Personality profile · {name}",
                    Content = row.Markdown,
                    ContentHash = RagChunker.Hash(row.Markdown),
                    CreatedAtUnixMs = row.Generated,
                    IsMarkdown = true,
                };
                await Writer.UpsertAsync(doc, ct);
            }

            if (maxSeen > watermark) await SetCursorAsync(cursor, maxSeen.ToString(), ct);
        }

        private static string PersonName(SqliteConnection conn, string personId)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT display_name FROM persons WHERE person_id=$p";
                cmd.Parameters.AddWithValue("$p", personId);
                return cmd.ExecuteScalar() as string ?? "someone";
            }
            catch { return "someone"; }
        }
    }
}
