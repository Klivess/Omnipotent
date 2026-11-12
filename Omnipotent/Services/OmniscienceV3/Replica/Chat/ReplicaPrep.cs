using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// What every live use of a replica resolves before composing (chat, Rehearsal, drafts, group simulation): who the
    /// addressee is (Klives by default, F309), names as of the cutoff (plan T5), the context kind ("auto" picks the
    /// context the person really uses with that addressee), MinorGuard at now and at the as-of, facts as of the cutoff and
    /// the query vectors.
    /// </summary>
    internal static class ReplicaPrep
    {
        /// <summary>Klives's own person: the self person this replica talked to most (several self accounts may exist unmerged).</summary>
        public static string? SelfPerson(ReplicaServices s, ReplicaSnapshot snapshot)
        {
            IReadOnlyList<string> selves;
            using (var corpus = s.Stores.Corpus.OpenRead()) selves = People.SelfPersons(corpus);
            if (selves.Count == 0) return null;
            return selves.OrderByDescending(id => snapshot.Visible.Count(p => p.PartnerPersonId == id)).ThenBy(id => id, StringComparer.Ordinal).First();
        }

        public static string NameAsOf(ReplicaServices s, string personId, long atMs)
        {
            using var corpus = s.Stores.Corpus.OpenRead();
            var person = People.Resolve(corpus, personId);
            if (person == null) return "someone";
            var book = new NameBook(corpus);
            book.Load(person.IdentityIds);
            return person.IdentityIds.Select(i => book.NameAt(i, atMs)).FirstOrDefault(n => n != null) ?? person.DisplayName;
        }

        public static string ResolveContext(string? requested, ReplicaSnapshot snapshot, string? addressee)
        {
            string kind = ContextKinds.Normalize(requested);
            if (kind != ContextKinds.Auto) return kind;
            var withThem = addressee == null ? new List<ReplicaPair>() : snapshot.Visible.Where(p => p.PartnerPersonId == addressee).ToList();
            var pool = withThem.Count >= 10 ? withThem : snapshot.Visible.ToList();
            return pool.Count == 0 ? ContextKinds.Dm : pool.GroupBy(p => p.ContextKind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        }

        /// <summary>MinorGuard at the moment of use: refused for a person who is, or may be, a minor now or at the as-of.</summary>
        public static string? MinorRefusal(ReplicaServices s, string personId, string name, long? asOfMs, long? adultFromMs)
        {
            long now = s.Clock.NowMs;
            var band = s.Kernel.Policy.BandOf(personId, now);
            if (MinorGuard.IsRestricted(band)) return $"Not available: the evidence says {name} is, or may be, under 18 ({band}). Omniscience never simulates a minor.";
            if (asOfMs is long at)
            {
                if (adultFromMs is long af && at < af) return $"Not available for {ReplicaComposer.Day(at)}: {name} was, or may have been, under 18 then.";
                var then = s.Kernel.Policy.BandOf(personId, at);
                if (MinorGuard.IsRestricted(then)) return $"Not available for {ReplicaComposer.Day(at)}: {name} was, or may have been, under 18 then ({then}).";
            }
            return null;
        }

        public static async Task<(float[]? Current, float[]? History)> VectorsAsync(ReplicaServices s, string current, string? history, CancellationToken ct)
        {
            if (s.Encoder() is not { } enc || string.IsNullOrWhiteSpace(current)) return (null, null);
            try
            {
                var texts = history == null ? new[] { current } : new[] { current, history };
                var v = await enc.EmbedAsync(texts, ct).ConfigureAwait(false);
                return (VecMath.Normalized(v[0]), history == null ? null : VecMath.Normalized(v[1]));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Retrieval falls back to BM25; the generation still runs.
                return (null, null);
            }
        }

        public static async Task<ComposeRequest> RequestAsync(ReplicaServices s, ReplicaSnapshot snapshot, long coreAsOfMs, long nowMs, string contextKind, string? addressee,
            string addresseeName, IReadOnlyList<ChatTurn> history, IReadOnlyList<string> latest, GenerationConfig config, string? memory, string? groupTranscript,
            string? instruction, CancellationToken ct)
        {
            var model = await s.ModelAsync(ct).ConfigureAwait(false);
            IReadOnlyList<PersonaFacts.Fact> facts;
            using (var intel = s.Stores.Intel.OpenRead()) facts = PersonaFacts.AsOf(intel, snapshot.Person.MemberIds, coreAsOfMs);
            string currentText = string.Join("\n", latest);
            string? historyText = history.Count == 0 ? null : string.Join("\n", history.TakeLast(4).Select(t => t.Text));
            var (cur, hist) = await VectorsAsync(s, currentText.Length > 0 ? currentText : historyText ?? "", historyText, ct).ConfigureAwait(false);
            return new ComposeRequest
            {
                Snapshot = snapshot,
                CoreAsOfMs = coreAsOfMs,
                NowMs = nowMs,
                ContextKind = contextKind,
                AddresseePersonId = addressee,
                AddresseeName = addresseeName,
                History = history,
                Latest = latest,
                MemorySummary = memory,
                GroupTranscript = groupTranscript,
                Instruction = instruction,
                Config = config,
                Model = model,
                Facts = facts,
                ZoneAt = s.ZoneFunc(snapshot.PersonId, snapshot.Person.IsSelf),
                Name = NameAsOf(s, snapshot.PersonId, coreAsOfMs),
                ReservedOutputTokens = s.Settings.ReservedOutputTokens,
                CurrentVector = cur,
                HistoryVector = hist,
            };
        }

        /// <summary>The live history budget the composer will apply (the orchestrator sizes the verbatim window with it).</summary>
        public static async Task<int> HistoryBudgetAsync(ReplicaServices s, CancellationToken ct)
        {
            var model = await s.ModelAsync(ct).ConfigureAwait(false);
            return (int)(ReplicaComposer.PromptBudget(model, s.Settings.ReservedOutputTokens, 1.0) * ReplicaComposer.HistoryShare);
        }

        public static AIRouterWorkClass Interactive => AIRouterWorkClass.Interactive;
    }
}
