using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// The blind test (plan "Evaluation": "a blind test where Klives guesses real vs replica"). Items are held-out replies
    /// from the serving version's evaluation: the moment before (names and text as they were), the real reply and the
    /// replica's, in an order fixed by a stable hash. The truth is revealed only after Klives answers; every answer is a
    /// gold label in operator.db keyed by natural keys (the reply's message ids), so it survives any rebuild (plan R3).
    /// </summary>
    public sealed class BlindTest
    {
        private sealed record Item(int Index, string PairId, string Stimulus, string A, string B, string RealIs, string? Answered, bool? Correct, string RealMessageIds);

        private readonly ReplicaServices s;

        public BlindTest(ReplicaServices services) => s = services;

        public async Task<ReplicaBlindSessionDto> StartAsync(string personId, int count, string actor, CancellationToken ct)
        {
            var person = s.Versions.Person(personId) ?? throw new ReplicaRefusedException("This person has no replica.", notFound: true);
            string versionId = person.ServingVersionId ?? throw new ReplicaRefusedException("This person has no serving replica yet.");
            count = Math.Clamp(count, 1, 40);
            List<(string PairId, string Generated)> evaluated;
            using (var c = s.Stores.Intel.OpenRead())
                evaluated = c.Query(null, """
                    SELECT pair_id, generated_text FROM replica_eval_items WHERE version_id=$v AND split='holdout_test' AND outcome IN ('ok','unjudged') AND generated_text IS NOT NULL
                    """, r => (r.GetString(0), r.GetString(1)), ("$v", versionId));
            if (evaluated.Count == 0) throw new ReplicaRefusedException("The serving version has no evaluated held-out replies yet; the blind test needs them.");
            var snapshot = s.Snapshots.Get(versionId);
            var evaluator = new ReplicaEvaluator(s);
            var items = new List<Item>();
            string sessionId = "rb_" + TextFeatures.Hash(versionId + "|" + s.Clock.NowMs + "|" + Guid.NewGuid().ToString("N"), 16);
            foreach (var (pairId, generated) in evaluated.OrderBy(e => TextFeatures.Unit(sessionId + "|" + e.PairId)).Take(count))
            {
                if (!snapshot.ById.TryGetValue(pairId, out var pair)) continue;
                var ctx = await evaluator.ContextAsync(snapshot, pair, pair.ReplyAtMs - 1, ct).ConfigureAwait(false);
                string stimulus = string.Join("\n", ctx.Messages.Select(m => (m.AuthorPersonId == snapshot.PersonId ? ctx.Name : m.Name) + ": " + m.Text));
                string real = string.Join("\n", pair.ReplyLines.Select(TextFeatures.ForPrompt));
                bool realIsA = TextFeatures.Unit("blind|" + sessionId + "|" + pairId) < 0.5;
                items.Add(new Item(items.Count, pairId, stimulus, realIsA ? real : generated, realIsA ? generated : real, realIsA ? "A" : "B", null, null, RJson.Write(pair.ReplyIds)));
            }
            long now = s.Clock.NowMs;
            await s.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO replica_blind_sessions(session_id, person_id, version_id, created_at_ms, items_json) VALUES($s,$p,$v,$t,$i)",
                ("$s", sessionId), ("$p", personId), ("$v", versionId), ("$t", now), ("$i", RJson.Write(items))), ct).ConfigureAwait(false);
            await OperatorAudit.WriteAsync(s.Stores.Operator, s.Clock, actor, "replica.blind.start", personId, new { sessionId, items = items.Count }, ct).ConfigureAwait(false);
            return Dto(sessionId, personId, versionId, now, items, null);
        }

        public async Task<ReplicaBlindAnswerDto> AnswerAsync(string sessionId, int index, string choice, string actor, CancellationToken ct)
        {
            string pick = (choice ?? "").Trim().ToUpperInvariant();
            if (pick is not ("A" or "B")) throw new ArgumentException("Choice must be A or B.");
            var (session, person, version, items) = Load(sessionId) ?? throw new KeyNotFoundException("No such blind test.");
            var item = items.FirstOrDefault(i => i.Index == index) ?? throw new KeyNotFoundException("No such item.");
            if (item.Answered != null) return new ReplicaBlindAnswerDto(index, item.Answered, item.RealIs, item.Correct == true, items.Count(i => i.Answered != null), items.Count(i => i.Correct == true));
            bool correct = pick == item.RealIs;
            items = items.Select(i => i.Index == index ? i with { Answered = pick, Correct = correct } : i).ToList();
            long now = s.Clock.NowMs;
            int answered = items.Count(i => i.Answered != null), right = items.Count(i => i.Correct == true);
            await s.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE replica_blind_sessions SET items_json=$i, answered=$a, correct=$c, finished_at_ms=CASE WHEN $a >= $n THEN $t ELSE finished_at_ms END WHERE session_id=$s
                """, ("$i", RJson.Write(items)), ("$a", answered), ("$c", right), ("$n", items.Count), ("$t", now), ("$s", sessionId)), ct).ConfigureAwait(false);
            // Gold label (natural key: the person and the real reply's message ids; never an intel row id).
            string key = Storage.Legacy.LegacyImporter.NaturalKey(new Dictionary<string, object?> { ["personId"] = person, ["realMessageIds"] = item.RealMessageIds, ["kind"] = "blind" });
            await s.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO gold_labels(kind, natural_key, verdict, correction_json, note, decided_at_ms, actor) VALUES('replica.blind_test',$k,$v,$j,NULL,$t,$a)
                """, ("$k", key), ("$v", correct ? "told_apart" : "fooled"), ("$j", RJson.Write(new { guessed = pick, real = item.RealIs, versionNumber = s.Versions.Version(version)?.Number })),
                ("$t", now), ("$a", actor)), ct).ConfigureAwait(false);
            return new ReplicaBlindAnswerDto(index, pick, item.RealIs, correct, answered, right);
        }

        public ReplicaBlindSessionDto? Get(string sessionId)
        {
            var loaded = Load(sessionId);
            if (loaded == null) return null;
            var (session, person, version, items) = loaded.Value;
            using var c = s.Stores.Intel.OpenRead();
            var row = c.Query(null, "SELECT created_at_ms, finished_at_ms FROM replica_blind_sessions WHERE session_id=$s", r => (r.GetInt64(0), r.GetInt64OrNull(1)), ("$s", sessionId)).First();
            return Dto(sessionId, person, version, row.Item1, items, row.Item2);
        }

        public ReplicaBlindResultsDto Results(string personId)
        {
            using var c = s.Stores.Intel.OpenRead();
            var rows = c.Query(null, "SELECT session_id FROM replica_blind_sessions WHERE person_id=$p ORDER BY created_at_ms DESC LIMIT 20", r => r.GetString(0), ("$p", personId));
            var sessions = rows.Select(Get).OfType<ReplicaBlindSessionDto>().ToList();
            int answered = sessions.Sum(x => x.Answered), correct = sessions.Sum(x => x.Correct);
            return new ReplicaBlindResultsDto(personId, sessions.Count, answered, correct, answered == 0 ? null : (double)correct / answered, sessions.Take(5).ToList());
        }

        private (string Session, string Person, string Version, List<Item> Items)? Load(string sessionId)
        {
            using var c = s.Stores.Intel.OpenRead();
            var row = c.Query(null, "SELECT person_id, version_id, items_json FROM replica_blind_sessions WHERE session_id=$s",
                r => (Person: r.GetString(0), Version: r.GetString(1), Items: r.GetString(2)), ("$s", sessionId)).FirstOrDefault();
            if (row.Person == null) return null;
            return (sessionId, row.Person, row.Version, RJson.Read<List<Item>>(row.Items) ?? new List<Item>());
        }

        /// <summary>The truth of an unanswered item is never in the response.</summary>
        private static ReplicaBlindSessionDto Dto(string sessionId, string person, string version, long created, List<Item> items, long? finished)
        {
            int answered = items.Count(i => i.Answered != null), correct = items.Count(i => i.Correct == true);
            return new ReplicaBlindSessionDto(sessionId, person, version, created,
                items.Select(i => new ReplicaBlindItemDto(i.Index, i.Stimulus, i.A, i.B, i.Answered, i.Answered == null ? null : i.Correct)).ToList(),
                answered, correct, answered == 0 ? null : (double)correct / answered, finished);
        }
    }
}
