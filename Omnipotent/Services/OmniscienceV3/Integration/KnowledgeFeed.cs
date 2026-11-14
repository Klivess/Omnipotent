using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge;
using Omnipotent.Services.OmniscienceV3.Knowledge.Api;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Retrieval;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Integration
{
    /// <summary>
    /// What Klives's other services may hold about people from v3 (KliveRAG, for his agents), and when they must let go of
    /// it (plan cutover; N20). Documents carry distilled knowledge only: one per person and category of their current stated
    /// facts that are accepted, or that Klives confirmed. A document never holds message text, an inference or a sensitive
    /// attribute, and none is made about someone who is forgotten, merged into another person, has analysis switched off, or
    /// is or may be a minor. Facts naming such a person are left out too, and every document says whom else it names
    /// (<see cref="FeedDocument.Names"/>), so a holder can tell when a document it keeps names someone it may no longer hold.
    /// A holder asks <see cref="MayServe"/> before it shows a document and deletes what it may no longer hold.
    /// </summary>
    public sealed class KnowledgeFeed
    {
        /// <summary>Every document id starts with this, then the person id, a colon and the category.</summary>
        public const string DocPrefix = "omni3fact:";

        /// <param name="Names">The other people the document's facts name (canonical person ids), besides the person it is about.</param>
        public sealed record FeedDocument(string DocId, string PersonId, string Title, string Text, long ChangedAtMs, IReadOnlyList<string> Names);

        /// <summary>One page of changes. The holder replaces everything it keeps about each of <paramref name="People"/> with
        /// that person's documents in <paramref name="Documents"/> (none: delete what it keeps about them).</summary>
        public sealed record FeedPage(IReadOnlyList<FeedDocument> Documents, IReadOnlyList<string> People, long Watermark, bool More);

        public sealed record FeedMessage(string MessageId, string Author, string Text, long SentAtMs, double Score);

        private readonly KnowledgeKit kit;

        public KnowledgeFeed(KnowledgeKit kit) => this.kit = kit;

        public KnowledgeFeed(V3Runtime runtime) : this(KnowledgeKit.For(runtime.Host.Context)) { }

        public static string DocId(string personId, string category) => DocPrefix + personId + ":" + category;

        /// <summary>The person a feed document is about (null when the id is not a feed document's).</summary>
        public static string? PersonOf(string docId)
        {
            if (!docId.StartsWith(DocPrefix, StringComparison.Ordinal)) return null;
            int colon = docId.LastIndexOf(':');
            return colon > DocPrefix.Length ? docId[DocPrefix.Length..colon] : null;
        }

        /// <summary>
        /// Whether another service may hold anything about this person now. False from the moment a forget is confirmed
        /// (the tombstone, or the analysis-off mark, is written with the confirmation; the run that removes things follows).
        /// </summary>
        public bool MayHold(string personId, long nowMs)
        {
            if (string.IsNullOrEmpty(personId)) return false;
            if (Forgotten.IsPerson(kit.Context.Stores.Operator, personId)) return false;                // forgotten, even while the run is still removing
            if (!kit.Corpus.PersonExists(personId)) return false;                                       // gone, or never known
            if (!string.Equals(kit.Corpus.Canonical(personId), personId, StringComparison.Ordinal)) return false; // merged: held under the person they joined
            if (kit.Corpus.AnalysisOff.Person(personId)) return false;
            return !MinorGuard.IsRestricted(kit.Context.Policy.BandOf(personId, nowMs));
        }

        /// <summary>Whether a document a holder keeps may be shown now: the person it is about, and everyone else it names.</summary>
        public bool MayServe(string docId, IEnumerable<string> names, long nowMs) =>
            PersonOf(docId) is { } person && MayHold(person, nowMs) && names.All(n => MayHold(n, nowMs));

        /// <summary>
        /// The people whose knowledge profile was rebuilt after <paramref name="watermark"/> (the profile's computed time), oldest
        /// first, about <paramref name="limit"/> of them (all those sharing the last one's time are included, so none is skipped),
        /// with the documents another service may hold about each now.
        /// </summary>
        public FeedPage ChangedSince(long watermark, int limit = 200)
        {
            long now = kit.Context.Clock.NowMs;
            List<(string Person, long At, string Body)> rows;
            bool more;
            using (var c = kit.Context.Stores.Intel.OpenRead())
            {
                const string Sql = "SELECT key, computed_at_ms, body FROM read_models WHERE model_id=$m AND computed_at_ms > $w ORDER BY computed_at_ms, key LIMIT $l";
                rows = c.Query(null, Sql, r => (r.GetString(0), r.GetInt64(1), r.GetString(2)), ("$m", DeductionModule.ProfileReadModel), ("$w", watermark), ("$l", Math.Max(1, limit)));
                if (rows.Count > 0)
                {
                    long last = rows[^1].At;
                    var seen = rows.Select(r => r.Person).ToHashSet(StringComparer.Ordinal);
                    rows.AddRange(c.Query(null, "SELECT key, computed_at_ms, body FROM read_models WHERE model_id=$m AND computed_at_ms = $t",
                        r => (r.GetString(0), r.GetInt64(1), r.GetString(2)), ("$m", DeductionModule.ProfileReadModel), ("$t", last)).Where(r => !seen.Contains(r.Item1)));
                    more = c.Scalar<long>(null, "SELECT COUNT(*) FROM read_models WHERE model_id=$m AND computed_at_ms > $t", ("$m", DeductionModule.ProfileReadModel), ("$t", last)) > 0;
                }
                else more = false;
            }

            var documents = new List<FeedDocument>();
            foreach (var (person, at, body) in rows) documents.AddRange(Build(person, at, body, now));
            return new FeedPage(documents, rows.Select(r => r.Person).Distinct(StringComparer.Ordinal).ToList(), rows.Count == 0 ? watermark : rows.Max(r => r.At), more);
        }

        /// <summary>
        /// The documents another service may hold about one person now, from their profile as it stands (none when there is
        /// no profile, or nothing may be held). A holder uses it to rebuild a document that named someone it may no longer
        /// hold, without waiting for the profile itself to be rebuilt.
        /// </summary>
        public IReadOnlyList<FeedDocument> DocumentsFor(string personId)
        {
            (long At, string? Body) row;
            using (var c = kit.Context.Stores.Intel.OpenRead())
                row = c.Query(null, "SELECT computed_at_ms, body FROM read_models WHERE model_id=$m AND key=$k", r => (r.GetInt64(0), r.GetString(1)),
                    ("$m", DeductionModule.ProfileReadModel), ("$k", personId)).FirstOrDefault();
            return row.Body == null ? Array.Empty<FeedDocument>() : Build(personId, row.At, row.Body, kit.Context.Clock.NowMs);
        }

        private List<FeedDocument> Build(string person, long at, string body, long now)
        {
            var documents = new List<FeedDocument>();
            if (!MayHold(person, now)) return documents;
            KnowledgeProfileDto? profile;
            try
            {
                profile = JsonSerializer.Deserialize<KnowledgeProfileDto>(body, KernelJson.Options);
            }
            catch (JsonException)
            {
                profile = null; // a profile this build cannot read gives nothing to hold until it is rebuilt
            }
            if (profile == null || profile.MinorRestricted) return documents;
            string name = kit.Corpus.DisplayName(person) ?? "Someone";
            foreach (var category in profile.Categories.Where(c => c.Category != Attributes.Sensitive))
            {
                var facts = category.Facts.Where(f => Holdable(f, now)).ToList();
                var lines = facts.Select(Line).Distinct(StringComparer.Ordinal).ToList();
                if (lines.Count == 0) continue;
                var names = facts.Where(f => f.ObjectPersonId != null).Select(f => kit.Corpus.Canonical(f.ObjectPersonId!)).Where(n => n != person)
                    .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
                documents.Add(new FeedDocument(DocId(person, category.Category), person, $"{name}: {category.Category}",
                    $"{name} — {category.Category}:\n" + string.Join("\n", lines.Select(l => "• " + l)), at, names));
            }
            return documents;
        }

        /// <summary>A current stated fact, accepted or confirmed, about nothing sensitive, naming nobody who may not be held.</summary>
        private bool Holdable(KnowledgeFactDto f, long now)
        {
            if (f.Status is not (AssertionStatus.Accepted or AssertionStatus.Confirmed)) return false;
            if (f.Sensitivity != "normal" || Attributes.IsSensitive(f.Attribute)) return false;
            if ((f.ValidToLatestMs ?? f.ValidToEarliestMs) is long ended && ended <= now) return false;
            return f.ObjectPersonId == null || MayHold(kit.Corpus.Canonical(f.ObjectPersonId), now);
        }

        private static string Line(KnowledgeFactDto f) =>
            $"{f.AttributeTitle}: {(f.Polarity == "negated" ? "not " : "")}{f.Value}{(f.Status == AssertionStatus.Confirmed ? " (confirmed by Klives)" : "")}";

        /// <summary>
        /// Messages matching <paramref name="query"/>, for a holder's explicit message search (KliveRAG's opt-in leg), at most
        /// <paramref name="limit"/>. Never a deleted message, and never one by someone forgotten (gone), with analysis off
        /// (the reader leaves them out), or who is or may be a minor; a message without a known author is left out too.
        /// </summary>
        public async Task<IReadOnlyList<FeedMessage>> SearchMessagesAsync(string query, int limit, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query) || limit <= 0) return Array.Empty<FeedMessage>();
            var retriever = new HybridRetriever(kit.Context.Stores, kit.Corpus, () => kit.Encoder);
            var (results, _) = await retriever.SearchAsync(new[] { query }, new RetrievalScope { Limit = Math.Clamp(limit * 3, 3, 60) }, ct).ConfigureAwait(false);
            if (results.Count == 0) return Array.Empty<FeedMessage>();
            var messages = kit.Corpus.Messages(results.Select(r => r.MessageId).ToList()).ToDictionary(m => m.MessageId, StringComparer.Ordinal);
            long now = kit.Context.Clock.NowMs;
            var hits = new List<FeedMessage>();
            foreach (var r in results)
            {
                if (!messages.TryGetValue(r.MessageId, out var m) || m.DeletedAtMs != null || string.IsNullOrWhiteSpace(m.Content) || m.AuthorPersonId == null) continue;
                string author = kit.Corpus.Canonical(m.AuthorPersonId);
                if (!MayHold(author, now)) continue;
                hits.Add(new FeedMessage(m.MessageId, kit.Corpus.DisplayName(author) ?? "someone", m.Content, m.SentAtMs, r.Score));
                if (hits.Count >= limit) break;
            }
            return hits;
        }
    }
}
