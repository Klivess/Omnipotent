using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    /// <summary>
    /// Puts people's names where what is stored carries their roster handles ("meet with p9a05 to understand the project
    /// system"): claims, facts, the results of tactic runs and the reports of inquiries. New claims are named as they
    /// are verified (<see cref="Verifier"/>) and new results as their run ends; this is for what was stored before that.
    /// Whose a handle is, and when it is left alone, is <see cref="HandleNamer"/>'s to say.
    /// <para>Only text that is shown changes. What a fact is keyed by (the normalised value, the natural key built on it)
    /// is left alone, so the fact stays the same fact: its plan, its place on the timeline and any decision Klives made
    /// about it keep pointing at it.</para>
    /// </summary>
    internal sealed class HandleRepair
    {
        /// <summary>What is gone through, in this order.</summary>
        public static readonly IReadOnlyList<string> Stages = new[] { "claims", "facts", "runs", "inquiries" };

        private readonly KnowledgeKit kit;
        private readonly HandleNamer namer;

        public HandleRepair(KnowledgeKit kit)
        {
            this.kit = kit;
            namer = new HandleNamer(kit.Corpus, kit.Context.Stores);
        }

        /// <param name="Looked">Rows read.</param>
        /// <param name="Rewritten">Rows whose text was changed.</param>
        /// <param name="LeftAsHandles">Handles that could not be given to one person, and stay.</param>
        /// <param name="People">The people whose facts changed (their views are to be built again).</param>
        /// <param name="Next">Where the next page starts; null when this was the last.</param>
        public sealed record Step(int Looked, int Rewritten, int LeftAsHandles, IReadOnlyCollection<string> People, string? Next);

        /// <summary>One page of a stage, in the order of its rows' ids.</summary>
        public Task<Step> StepAsync(string stage, string after, int page, CancellationToken ct) => stage switch
        {
            "claims" => ClaimsAsync(after, page, ct),
            "facts" => FactsAsync(after, page, ct),
            "runs" => RunsAsync(after, Math.Max(1, page / 10), ct),
            "inquiries" => InquiriesAsync(after, Math.Max(1, page / 10), ct),
            _ => throw new ArgumentException($"Unknown stage '{stage}'.", nameof(stage)),
        };

        /// <summary>One page of claims.</summary>
        public async Task<Step> ClaimsAsync(string after, int page, CancellationToken ct)
        {
            List<(string Id, string Text)> rows;
            var about = new Dictionary<string, (string Person, string? Object, string? Author, string? Conversation, string? Detail)>(StringComparer.Ordinal);
            var cited = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (var c = kit.Context.Stores.Intel.OpenRead())
            {
                rows = c.Query(null, "SELECT claim_id, value_text FROM kn_claims WHERE claim_id > $a AND value_text IS NOT NULL ORDER BY claim_id LIMIT $l",
                    r => (r.GetString(0), r.GetString(1)), ("$a", after), ("$l", page));
                var held = rows.Where(r => HandleText.MayHold(r.Text)).Select(r => r.Id).ToList();
                foreach (var row in c.QueryIn(null, "SELECT claim_id, person_id, object_id, author_person_id, conversation_id, detail_json FROM kn_claims WHERE claim_id IN ({keys})", held,
                             r => (Id: r.GetString(0), Person: r.GetString(1), Object: r.GetStringOrNull(2), Author: r.GetStringOrNull(3), Conversation: r.GetStringOrNull(4), Detail: r.GetStringOrNull(5))))
                    about[row.Id] = (row.Person, row.Object, row.Author, row.Conversation, row.Detail);
                foreach (var (id, message) in c.QueryIn(null, "SELECT claim_id, message_id FROM kn_claim_messages WHERE claim_id IN ({keys})", held, r => (r.GetString(0), r.GetString(1))))
                    (cited.TryGetValue(id, out var list) ? list : cited[id] = new List<string>()).Add(message);
            }
            var tally = new HandleNamer.Tally();
            var changed = new List<(string Id, string Text)>();
            foreach (var (id, text) in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (!about.TryGetValue(id, out var a)) continue;
                var (promiser, promisee) = Promise(a.Detail);
                string named = namer.Text(text, new[] { a.Person, a.Object, a.Author, promiser, promisee }, One(a.Conversation), cited.GetValueOrDefault(id), tally);
                if (!string.Equals(named, text, StringComparison.Ordinal)) changed.Add((id, named));
            }
            if (changed.Count > 0)
                await kit.Context.Stores.Intel.WriteAsync((c, tx) =>
                {
                    using var put = new PreparedStatement(c, tx, "UPDATE kn_claims SET value_text=$t WHERE claim_id=$i", "$t", "$i");
                    foreach (var (id, text) in changed) put.Run(text, id);
                }, ct).ConfigureAwait(false);
            return new Step(rows.Count, changed.Count, tally.Left, Array.Empty<string>(), rows.Count < page ? null : rows[^1].Id);
        }

        /// <summary>One page of facts. Every version is put right, not only the one that holds now: a fact's history is read too.</summary>
        public async Task<Step> FactsAsync(string after, int page, CancellationToken ct)
        {
            List<(string Id, string Subject, string? Object, string? Text)> rows;
            var where = new Dictionary<string, (string? Author, string? Conversation)>(StringComparer.Ordinal);
            var cited = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (var c = kit.Context.Stores.Intel.OpenRead())
            {
                rows = c.Query(null, "SELECT assertion_id, subject_id, object_id, value_text FROM assertions WHERE assertion_id > $a AND subject_kind='person' ORDER BY assertion_id LIMIT $l",
                    r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3)), ("$a", after), ("$l", page));
                var held = rows.Where(r => HandleText.MayHold(r.Text)).Select(r => r.Id).ToList();
                foreach (var (id, author, conversation) in c.QueryIn(null, "SELECT assertion_id, author_person_id, conversation_id FROM kn_assertion_dims WHERE assertion_id IN ({keys})", held,
                             r => (r.GetString(0), r.GetStringOrNull(1), r.GetStringOrNull(2))))
                    where[id] = (author, conversation);
                foreach (var (id, message) in c.QueryIn(null, "SELECT assertion_id, message_id FROM assertion_evidence WHERE assertion_id IN ({keys})", held, r => (r.GetString(0), r.GetString(1))))
                    (cited.TryGetValue(id, out var list) ? list : cited[id] = new List<string>()).Add(message);
            }
            var tally = new HandleNamer.Tally();
            var changed = new List<(string Id, string Subject, string Text)>();
            foreach (var (id, subject, obj, text) in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (text == null || !HandleText.MayHold(text)) continue;
                where.TryGetValue(id, out var w);
                string named = namer.Text(text, new[] { subject, obj, w.Author }, One(w.Conversation), cited.GetValueOrDefault(id), tally);
                if (!string.Equals(named, text, StringComparison.Ordinal)) changed.Add((id, subject, named));
            }
            if (changed.Count > 0)
                await kit.Context.Stores.Intel.WriteAsync((c, tx) =>
                {
                    // The text as a column and the text inside the stored value are the same words; the value's key form is not touched.
                    using var put = new PreparedStatement(c, tx, "UPDATE assertions SET value_text=$t, value_json=json_set(value_json, '$.text', $t) WHERE assertion_id=$i", "$t", "$i");
                    foreach (var (id, _, text) in changed) put.Run(text, id);
                }, ct).ConfigureAwait(false);
            var people = changed.Select(x => kit.Corpus.Canonical(x.Subject)).Distinct(StringComparer.Ordinal).ToList();
            return new Step(rows.Count, changed.Count, tally.Left, people, rows.Count < page ? null : rows[^1].Id);
        }

        /// <summary>One page of tactic runs: the results they ended with. Who a result concerns and where it comes from is
        /// read from the run's input and from the result itself.</summary>
        public async Task<Step> RunsAsync(string after, int page, CancellationToken ct)
        {
            if (!Has("tactic_runs")) return new Step(0, 0, 0, Array.Empty<string>(), null);
            List<(string Id, string Input, string Outputs)> rows;
            using (var c = kit.Context.Stores.Intel.OpenRead())
                rows = c.Query(null, "SELECT run_id, input_json, outputs_json FROM tactic_runs WHERE run_id > $a AND outputs_json IS NOT NULL ORDER BY run_id LIMIT $l",
                    r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$a", after), ("$l", page));
            var tally = new HandleNamer.Tally();
            var changed = new List<(string Id, string Json)>();
            foreach (var (id, input, outputs) in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (!HandleText.MayHold(outputs)) continue;
                string named = namer.Json(outputs, namer.PeopleIn(input), tally);
                if (!ReferenceEquals(named, outputs)) changed.Add((id, named));
            }
            if (changed.Count > 0)
                await kit.Context.Stores.Intel.WriteAsync((c, tx) =>
                {
                    using var put = new PreparedStatement(c, tx, "UPDATE tactic_runs SET outputs_json=$o WHERE run_id=$i", "$o", "$i");
                    foreach (var (id, json) in changed) put.Run(json, id);
                }, ct).ConfigureAwait(false);
            return new Step(rows.Count, changed.Count, tally.Left, Array.Empty<string>(), rows.Count < page ? null : rows[^1].Id);
        }

        /// <summary>One page of inquiries: the report each ended with, and the question as it was restated.</summary>
        public async Task<Step> InquiriesAsync(string after, int page, CancellationToken ct)
        {
            if (!Has("kn_inquiries")) return new Step(0, 0, 0, Array.Empty<string>(), null);
            List<(string Id, string? Subject, string Subjects, string? Restatement, string? Report)> rows;
            using (var c = kit.Context.Stores.Intel.OpenRead())
                rows = c.Query(null, "SELECT inquiry_id, primary_subject_id, subjects_json, restatement, report_json FROM kn_inquiries WHERE inquiry_id > $a ORDER BY inquiry_id LIMIT $l",
                    r => (r.GetString(0), r.GetStringOrNull(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4)), ("$a", after), ("$l", page));
            var tally = new HandleNamer.Tally();
            var changed = new List<(string Id, string? Restatement, string? Report)>();
            foreach (var (id, subject, subjects, restatement, report) in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (!HandleText.MayHold(restatement) && !HandleText.MayHold(report)) continue;
                var concerned = namer.PeopleIn(subjects).Append(subject).OfType<string>().ToList();
                string? namedRestatement = restatement == null ? null : namer.Text(restatement, concerned, tally: tally);
                string? namedReport = report == null ? null : namer.Json(report, concerned, tally);
                if (!string.Equals(namedRestatement, restatement, StringComparison.Ordinal) || !string.Equals(namedReport, report, StringComparison.Ordinal))
                    changed.Add((id, namedRestatement, namedReport));
            }
            if (changed.Count > 0)
                await kit.Context.Stores.Intel.WriteAsync((c, tx) =>
                {
                    using var put = new PreparedStatement(c, tx, "UPDATE kn_inquiries SET restatement=$r, report_json=$j WHERE inquiry_id=$i", "$r", "$j", "$i");
                    foreach (var (id, restatement, report) in changed) put.Run(restatement, report, id);
                }, ct).ConfigureAwait(false);
            return new Step(rows.Count, changed.Count, tally.Left, Array.Empty<string>(), rows.Count < page ? null : rows[^1].Id);
        }

        /// <summary>Whether the engine this runs in has the table (the tactic runner and the inquiry module are modules of their own).</summary>
        private bool Has(string table)
        {
            using var c = kit.Context.Stores.Intel.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t", ("$t", table)) > 0;
        }

        private static IReadOnlyCollection<string>? One(string? conversation) => conversation == null ? null : new[] { conversation };

        private static (string? Promiser, string? Promisee) Promise(string? detailJson)
        {
            if (string.IsNullOrWhiteSpace(detailJson)) return (null, null);
            try
            {
                using var doc = JsonDocument.Parse(detailJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
                string? Text(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                return (Text("promiserPersonId"), Text("promiseePersonId"));
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
    }
}
