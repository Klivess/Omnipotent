using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Api
{
    /// <summary>
    /// What review items point at, named. A review item's detail is stored as the module raised it, with ids in it (an
    /// alias, a server, claims and messages, two accounts); nobody can recognise an id, so this turns each into what a
    /// person reads and acts on: the people by name, the server or chat by its name, the messages it rests on with who said
    /// them, where and when, and for the kinds with a decision of their own, the thing to decide (the name, the fact, the
    /// age statements, the two accounts). A page of items costs a handful of batched reads, never one per id.
    /// </summary>
    public sealed class ReviewContexts
    {
        /// <summary>The kinds whose item is about one fact, found again by the item's natural key.</summary>
        private static readonly HashSet<string> FactKinds = new(StringComparer.Ordinal)
        {
            ReviewKinds.Contradiction, ReviewKinds.Flapping, ReviewKinds.Unproven, ReviewKinds.PossibleChange, ReviewKinds.RejectedResurfaced,
        };

        /// <summary>Quoted messages shown per item: enough to judge by, never the whole history.</summary>
        internal const int MessagesPerItem = 6;

        private readonly KnowledgeKit kit;
        private readonly OmniscienceStores stores;

        public ReviewContexts(KnowledgeKit kit, OmniscienceStores stores)
        {
            this.kit = kit;
            this.stores = stores;
        }

        /// <summary>The review items of a page as the API serves them, each with its <see cref="ReviewContextDto"/>.</summary>
        public IReadOnlyList<KnowledgeReviewItemDto> Dtos(IReadOnlyList<ReviewItem> items)
        {
            var contexts = For(items);
            return items.Select(i => new KnowledgeReviewItemDto(i.ItemId, i.Kind, i.SubjectId, i.Title, i.DetailJson, i.Priority, i.State, i.CreatedAtMs, i.UpdatedAtMs,
                i.ResolvedAtMs, i.Resolution, i.Actor, contexts.GetValueOrDefault(i.ItemId))).ToList();
        }

        /// <summary>The context of each item, by item id.</summary>
        public IReadOnlyDictionary<string, ReviewContextDto> For(IReadOnlyList<ReviewItem> items)
        {
            var parsed = items.Select(i => (Item: i, Detail: Parse(i.DetailJson))).ToList();

            // ── what each item refers to ─────────────────────────────────────────
            var aliasIds = new HashSet<string>(StringComparer.Ordinal);
            var pairs = new List<(string A, string B)>();
            var ageIds = new HashSet<string>(StringComparer.Ordinal);
            var claimIds = new HashSet<string>(StringComparer.Ordinal);
            var factKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (item, d) in parsed)
            {
                switch (item.Kind)
                {
                    case ReviewKinds.Alias when Str(d, "aliasId") is { } alias: aliasIds.Add(alias); break;
                    case ReviewKinds.IdentityLink when Str(d, "identityA") is { } a && Str(d, "identityB") is { } b: pairs.Add((a, b)); break;
                    case ReviewKinds.AgeConflict: foreach (var id in AgeIds(d)) ageIds.Add(id); break;
                    case ReviewKinds.Contradiction: foreach (var id in Strings(d, "claims")) claimIds.Add(id); break;
                }
                if (FactKinds.Contains(item.Kind) && item.NaturalKey != null) factKeys.Add(item.NaturalKey);
            }

            var aliases = Aliases(aliasIds);
            var links = Links(pairs);
            var ages = AgeStatements(ageIds);
            var claims = Claims(claimIds);
            var facts = Facts(factKeys);

            // ── the messages each item rests on, read once for the page ──────────
            var refsByItem = new List<(string ItemId, List<EvidenceRef> Refs)>();
            foreach (var (item, d) in parsed)
            {
                var refs = new List<EvidenceRef>();
                switch (item.Kind)
                {
                    case ReviewKinds.Alias when Str(d, "aliasId") is { } alias && aliases.TryGetValue(alias, out var a):
                        refs.AddRange(a.Evidence);
                        break;
                    case ReviewKinds.AgeConflict:
                        foreach (var id in AgeIds(d))
                            if (ages.TryGetValue(id, out var s)) refs.Add(new EvidenceRef(s.MessageId, s.MessageVersion, EvidenceRoles.Support, null, 1, s.AssertedAtMs));
                        break;
                    case ReviewKinds.Contradiction:
                        foreach (var id in Strings(d, "claims"))
                            if (claims.TryGetValue(id, out var cl)) refs.AddRange(cl.Messages.Select(m => new EvidenceRef(m, 0, EvidenceRoles.Support, null, 1)));
                        break;
                    case ReviewKinds.Hypothesis:
                        refs.AddRange(Strings(d, "messages").Select(m => new EvidenceRef(m, 0, EvidenceRoles.Support, null, 1)));
                        break;
                }
                refsByItem.Add((item.ItemId, refs.GroupBy(r => r.MessageId, StringComparer.Ordinal).Select(g => g.First()).Take(MessagesPerItem).ToList()));
            }
            var all = EvidenceContexts.List(kit, refsByItem.SelectMany(r => r.Refs).ToList(), Names, citedVersions: false);
            var quotedByItem = new Dictionary<string, IReadOnlyList<EvidenceContextDto>>(StringComparer.Ordinal);
            int at = 0;
            foreach (var (itemId, refs) in refsByItem)
            {
                quotedByItem[itemId] = all.Skip(at).Take(refs.Count).ToList();
                at += refs.Count;
            }

            // ── the people and places they name ──────────────────────────────────
            var people = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (item, _) in parsed) if (item.SubjectId != null) people.Add(item.SubjectId);
            foreach (var l in links.Values) { people.Add(l.PersonA); people.Add(l.PersonB); }
            foreach (var f in facts.Values) if (f.ObjectId != null) people.Add(f.ObjectId);
            foreach (var m in all) if (m.AuthorPersonId != null) people.Add(m.AuthorPersonId);
            var names = Names(people);
            var places = Places(aliases.Values.Select(a => (a.ScopeKind, a.ScopeId)));
            var accounts = Accounts(links.Values.SelectMany(l => new[] { l.IdentityA, l.IdentityB }).ToList());

            // ── one context per item ─────────────────────────────────────────────
            var result = new Dictionary<string, ReviewContextDto>(StringComparer.Ordinal);
            foreach (var (item, d) in parsed)
            {
                var quoted = quotedByItem[item.ItemId];
                var mentioned = new HashSet<string>(StringComparer.Ordinal);
                if (item.SubjectId != null) mentioned.Add(item.SubjectId);
                foreach (var m in quoted) if (m.AuthorPersonId != null) mentioned.Add(m.AuthorPersonId);

                ReviewAliasDto? alias = null;
                if (item.Kind == ReviewKinds.Alias && Str(d, "aliasId") is { } aliasId && aliases.TryGetValue(aliasId, out var a))
                {
                    var place = places.GetValueOrDefault((a.ScopeKind, a.ScopeId ?? ""));
                    alias = new ReviewAliasDto(a.AliasId, a.Alias, a.NameKind, a.Status, a.ScopeKind, a.ScopeId, place.Label, place.Kind, a.Usages, a.Speakers, a.SelfClaims, a.CommonWord,
                        a.FirstSeenMs, a.LastSeenMs);
                }

                ReviewLinkDto? link = null;
                if (item.Kind == ReviewKinds.IdentityLink && Str(d, "identityA") is { } ia && Str(d, "identityB") is { } ib && links.TryGetValue(PairKey(ia, ib), out var l))
                {
                    mentioned.Add(l.PersonA);
                    mentioned.Add(l.PersonB);
                    link = new ReviewLinkDto(l.LinkId, l.Verdict, accounts.GetValueOrDefault(l.IdentityA) ?? Forgotten(l.IdentityA, l.PersonA),
                        accounts.GetValueOrDefault(l.IdentityB) ?? Forgotten(l.IdentityB, l.PersonB), l.Probability, l.IndependentSignals, l.Signals, l.MergeId);
                }

                IReadOnlyList<AgeStatementDto>? age = null;
                if (item.Kind == ReviewKinds.AgeConflict)
                {
                    age = AgeIds(d).Select(id => ages.GetValueOrDefault(id)).OfType<AgeEvidenceRow>()
                        .Select(s => new AgeStatementDto(s.EvidenceId, s.Kind, s.Status, s.SourceKind, s.AssertedAtMs, s.MessageId, s.Probability,
                            quoted.FirstOrDefault(q => q.MessageId == s.MessageId)?.Text is { } t ? EvidenceRenderer.Truncate(t, 400) : null)).ToList();
                }

                ReviewFactDto? fact = null;
                if (FactKinds.Contains(item.Kind) && Str(d, "attribute") is { } attribute)
                {
                    var current = item.NaturalKey != null ? facts.GetValueOrDefault(item.NaturalKey) : null;
                    if (current?.ObjectId != null) mentioned.Add(current.ObjectId);
                    var values = new List<string>();
                    void Add(string? v) { if (!string.IsNullOrWhiteSpace(v) && !values.Contains(v, StringComparer.OrdinalIgnoreCase)) values.Add(v); }
                    Add(Str(d, "confirmed"));
                    Add(Str(d, "newer"));
                    Add(Str(d, "value"));
                    foreach (var id in Strings(d, "claims")) Add(claims.GetValueOrDefault(id)?.Value);
                    fact = new ReviewFactDto(attribute, Attributes.Get(attribute).Title, current?.AssertionId, current?.Value ?? Str(d, "value"), current?.Status, current?.ObjectId, values);
                }

                result[item.ItemId] = new ReviewContextDto(
                    mentioned.Where(names.ContainsKey).ToDictionary(p => p, p => names[p], StringComparer.Ordinal), quoted, alias, fact, age, link);
            }
            return result;
        }

        // ── reads ────────────────────────────────────────────────────────────────

        private sealed record AliasRead(string AliasId, string Alias, string NameKind, string ScopeKind, string? ScopeId, long FirstSeenMs, long LastSeenMs, int Usages, int Speakers,
            int SelfClaims, string Status, bool CommonWord, IReadOnlyList<EvidenceRef> Evidence);

        private Dictionary<string, AliasRead> Aliases(IReadOnlyCollection<string> ids)
        {
            if (ids.Count == 0) return new(StringComparer.Ordinal);
            using var c = stores.Intel.OpenRead();
            return c.QueryIn(null, """
                    SELECT alias_id, alias, name_kind, scope_kind, scope_id, first_seen_ms, last_seen_ms, usages, speakers, self_claims, status, common_word, evidence_json
                    FROM kn_aliases WHERE alias_id IN ({keys})
                    """, ids, r => new AliasRead(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetInt64(5), r.GetInt64(6), r.GetInt32(7),
                    r.GetInt32(8), r.GetInt32(9), r.GetString(10), r.GetInt32(11) == 1, AliasEvidence(r.GetStringOrNull(12))))
                .ToDictionary(a => a.AliasId, StringComparer.Ordinal);
        }

        /// <summary>The usages an alias was learned from (stored as the engine found them: message, version, quote, speaker, time), newest first.</summary>
        private static IReadOnlyList<EvidenceRef> AliasEvidence(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<EvidenceRef>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<EvidenceRef>();
                return doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Object && Str(e, "messageId") != null)
                    .Select(e => new EvidenceRef(Str(e, "messageId")!, e.TryGetProperty("version", out var v) && v.TryGetInt32(out int ver) ? ver : 0, EvidenceRoles.Support, Str(e, "quote"), 1,
                        e.TryGetProperty("sentAtMs", out var t) && t.TryGetInt64(out long sent) ? sent : null))
                    .OrderByDescending(e => e.SentAtMs ?? 0)
                    .ToList();
            }
            catch (JsonException)
            {
                return Array.Empty<EvidenceRef>();
            }
        }

        private sealed record LinkRead(string LinkId, string IdentityA, string IdentityB, string PersonA, string PersonB, IReadOnlyList<LinkSignalDto> Signals, int IndependentSignals,
            double Probability, string Verdict, long? MergeId);

        private static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "\u0001" + b : b + "\u0001" + a;

        private Dictionary<string, LinkRead> Links(IReadOnlyList<(string A, string B)> pairs)
        {
            var found = new Dictionary<string, LinkRead>(StringComparer.Ordinal);
            if (pairs.Count == 0) return found;
            var wanted = pairs.Select(p => PairKey(p.A, p.B)).ToHashSet(StringComparer.Ordinal);
            var identities = pairs.SelectMany(p => new[] { p.A, p.B }).Distinct(StringComparer.Ordinal).ToList();
            using var c = stores.Intel.OpenRead();
            foreach (var row in c.QueryIn(null, """
                         SELECT link_id, identity_a, identity_b, person_a, person_b, signals_json, independent_signals, probability, verdict, merge_id
                         FROM kn_identity_links WHERE identity_a IN ({keys})
                         """, identities, r => new LinkRead(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), Signals(r.GetStringOrNull(5)),
                         r.GetInt32(6), r.GetDouble(7), r.GetString(8), r.GetInt64OrNull(9))))
            {
                string key = PairKey(row.IdentityA, row.IdentityB);
                if (wanted.Contains(key)) found[key] = row;
            }
            return found;
        }

        private static IReadOnlyList<LinkSignalDto> Signals(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<LinkSignalDto>();
            try
            {
                return (JsonSerializer.Deserialize<List<Entities.LinkSignal>>(json, KernelJson.Options) ?? new()).Select(s => new LinkSignalDto(s.Kind, s.Strength, s.Detail)).ToList();
            }
            catch (JsonException)
            {
                return Array.Empty<LinkSignalDto>();
            }
        }

        private Dictionary<string, AgeEvidenceRow> AgeStatements(IReadOnlyCollection<string> ids)
        {
            if (ids.Count == 0) return new(StringComparer.Ordinal);
            using var c = stores.Intel.OpenRead();
            return c.QueryIn(null, """
                    SELECT evidence_id, person_id, kind, birth_earliest_ms, birth_latest_ms, month, day, source_kind, asserted_at_ms, message_id, message_version, claim_id, probability, status, natural_key
                    FROM kn_age_evidence WHERE evidence_id IN ({keys})
                    """, ids, EvidencePolicyOracle.Map)
                .ToDictionary(r => r.EvidenceId, StringComparer.Ordinal);
        }

        private sealed record ClaimRead(string ClaimId, string? Value, List<string> Messages);

        private Dictionary<string, ClaimRead> Claims(IReadOnlyCollection<string> ids)
        {
            if (ids.Count == 0) return new(StringComparer.Ordinal);
            using var c = stores.Intel.OpenRead();
            var claims = c.QueryIn(null, "SELECT claim_id, value_text FROM kn_claims WHERE claim_id IN ({keys})", ids, r => new ClaimRead(r.GetString(0), r.GetStringOrNull(1), new List<string>()))
                .ToDictionary(x => x.ClaimId, StringComparer.Ordinal);
            foreach (var (claim, message) in c.QueryIn(null, "SELECT claim_id, message_id FROM kn_claim_messages WHERE claim_id IN ({keys})", ids, r => (r.GetString(0), r.GetString(1))))
                if (claims.TryGetValue(claim, out var cl)) cl.Messages.Add(message);
            return claims;
        }

        private sealed record FactRead(string AssertionId, string? Value, string Status, string? ObjectId);

        /// <summary>The version of each fact that stands now (neither replaced nor withdrawn), else its newest version.</summary>
        private Dictionary<string, FactRead> Facts(IReadOnlyCollection<string> naturalKeys)
        {
            if (naturalKeys.Count == 0) return new(StringComparer.Ordinal);
            using var c = stores.Intel.OpenRead();
            return c.QueryIn(null, """
                    SELECT natural_key, assertion_id, value_text, status, object_id, superseded_at_ms, retracted_at_ms, recorded_at_ms
                    FROM assertions WHERE natural_key IN ({keys})
                    """, naturalKeys, r => (Key: r.GetString(0), Fact: new FactRead(r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetStringOrNull(4)),
                    Live: r.IsDBNull(5) && r.IsDBNull(6), At: r.GetInt64(7)))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Live).ThenByDescending(x => x.At).First().Fact, StringComparer.Ordinal);
        }

        /// <summary>Display names of people (the canonical person's when one was merged into another). Someone with no name is absent.</summary>
        public IReadOnlyDictionary<string, string> Names(IReadOnlyCollection<string> personIds)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (personIds.Count == 0) return names;
            var canonical = personIds.Distinct(StringComparer.Ordinal).ToDictionary(p => p, kit.Corpus.Canonical, StringComparer.Ordinal);
            Dictionary<string, string> byCanonical;
            using (var c = stores.Corpus.OpenRead())
                byCanonical = c.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys}) AND display_name IS NOT NULL",
                        canonical.Values.Distinct(StringComparer.Ordinal).ToList(), r => (Id: r.GetString(0), Name: r.GetString(1)))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                    .GroupBy(x => x.Id, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
            foreach (var (id, canon) in canonical)
                if (byCanonical.TryGetValue(canon, out var name)) names[id] = name;
            return names;
        }

        /// <summary>Each person's own display name, not the name of whoever they were merged into (the two sides of a merge).</summary>
        public IReadOnlyDictionary<string, string> OwnNames(IReadOnlyCollection<string> personIds)
        {
            var ids = personIds.Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys}) AND display_name IS NOT NULL", ids, r => (Id: r.GetString(0), Name: r.GetString(1)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        }

        /// <summary>Servers and chats by name: a server by its own name, a channel with its server, a direct or group chat by who is in it.</summary>
        private Dictionary<(string Kind, string Id), (string? Label, string? Kind)> Places(IEnumerable<(string ScopeKind, string? ScopeId)> scopes)
        {
            var result = new Dictionary<(string, string), (string?, string?)>();
            var list = scopes.Where(s => !string.IsNullOrEmpty(s.ScopeId)).Distinct().ToList();
            var guilds = list.Where(s => s.ScopeKind == "guild").Select(s => s.ScopeId!).Distinct(StringComparer.Ordinal).ToList();
            var conversations = list.Where(s => s.ScopeKind != "guild").Select(s => s.ScopeId!).Distinct(StringComparer.Ordinal).ToList();
            var lookups = new Lookups(stores);
            if (guilds.Count > 0)
                foreach (var g in lookups.Guilds(null, Array.Empty<string>(), guilds, 200).Items)
                    result[("guild", g.Id)] = (g.Label, ScopePlaces.Server);
            if (conversations.Count > 0)
                foreach (var o in lookups.Conversations(null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), conversations, 200, namePeople: true).Items)
                    result[("conversation", o.Id)] = (o.Kind == ScopePlaces.Server && o.GroupLabel != null ? $"{o.Label} · {o.GroupLabel}" : o.Label, o.Kind);
            return result;
        }

        /// <summary>Accounts by what they are called: the platform, the name the account shows now, its handle, and whose it is.</summary>
        public IReadOnlyDictionary<string, ReviewAccountDto> Accounts(IReadOnlyCollection<string> identityIds)
        {
            var result = new Dictionary<string, ReviewAccountDto>(StringComparer.Ordinal);
            var ids = identityIds.Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return result;
            List<IdentityInfo> identities;
            using (var c = stores.Corpus.OpenRead())
                identities = c.QueryIn(null, "SELECT identity_id, platform, platform_user_id, person_id, kind FROM identities WHERE identity_id IN ({keys})", ids,
                    r => new IdentityInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            var names = kit.Corpus.NamesOf(ids).GroupBy(n => n.IdentityId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var i in identities)
            {
                var own = names.GetValueOrDefault(i.IdentityId) ?? new List<IdentityName>();
                // What it is called now (the newest of each kind), the shown name before the handle.
                string? Newest(params string[] kinds) => own.Where(n => kinds.Contains(n.Kind, StringComparer.Ordinal))
                    .OrderBy(n => n.ValidToMs == null ? 0 : 1).ThenByDescending(n => n.ValidFromMs).Select(n => n.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                string? handle = Newest("username");
                string? shown = Newest("display", "global", "global_name", "nickname") ?? handle;
                result[i.IdentityId] = new ReviewAccountDto(i.Platform, shown, handle != null && !string.Equals(handle, shown, StringComparison.Ordinal) ? handle : null,
                    kit.Corpus.Canonical(i.PersonId));
            }
            return result;
        }

        /// <summary>An account the corpus no longer has (forgotten): its platform, from the id, and nothing that names it.</summary>
        private static ReviewAccountDto Forgotten(string identityId, string personId)
        {
            int colon = identityId.IndexOf(':');
            return new ReviewAccountDto(colon > 0 ? identityId[..colon] : "unknown", null, null, personId);
        }

        // ── detail JSON ──────────────────────────────────────────────────────────

        private static JsonElement? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IEnumerable<string> AgeIds(JsonElement? d) => Objects(d, "inconsistent").Select(e => Str(e, "evidenceId")).OfType<string>();

        private static string? Str(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static IEnumerable<string> Strings(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : Enumerable.Empty<string>();

        private static IEnumerable<JsonElement> Objects(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList()
                : Enumerable.Empty<JsonElement>();
    }

    /// <summary>Cited messages in context (who said them, where, when, the words), read once for a set of references.</summary>
    public static class EvidenceContexts
    {
        /// <summary>One context per reference, in order. A message that cannot be read any more is still listed, without its words.</summary>
        /// <param name="names">Display names for author ids, asked for once for every author.</param>
        /// <param name="citedVersions">The words of the version each reference cites (one read per reference: a fact's evidence);
        /// otherwise the message as it reads now, and an edit since is flagged.</param>
        public static IReadOnlyList<EvidenceContextDto> List(KnowledgeKit kit, IReadOnlyList<EvidenceRef> evidence, Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, string>> names,
            bool citedVersions = true)
        {
            if (evidence.Count == 0) return Array.Empty<EvidenceContextDto>();
            var messages = kit.Corpus.Messages(evidence.Select(e => e.MessageId).Distinct(StringComparer.Ordinal).ToList()).ToDictionary(m => m.MessageId, StringComparer.Ordinal);
            var conversations = kit.Corpus.Conversations(messages.Values.Select(m => m.ConversationId).Distinct(StringComparer.Ordinal).ToList());
            var authors = names(messages.Values.Select(m => m.AuthorPersonId).OfType<string>().Distinct(StringComparer.Ordinal).ToList());
            return evidence.Select(e =>
            {
                messages.TryGetValue(e.MessageId, out var m);
                var conv = m != null && conversations.TryGetValue(m.ConversationId, out var cv) ? cv : null;
                string? text = m == null ? null : (citedVersions && e.MessageVersion > 0 ? kit.Corpus.VersionText(e.MessageId, e.MessageVersion) : null) ?? m.Content;
                return new EvidenceContextDto(e.MessageId, e.MessageVersion, e.Role, e.Quote, e.Weight, e.SentAtMs ?? m?.SentAtMs, m?.AuthorPersonId,
                    m?.AuthorPersonId == null ? null : authors.GetValueOrDefault(m.AuthorPersonId), m?.ConversationId, conv?.Kind, conv?.Title, m?.GuildId, conv?.GuildName, m?.Platform,
                    m?.DeletedAtMs != null, m?.EditedAtMs != null, text == null ? null : EvidenceRenderer.Truncate(text, 2_000));
            }).ToList();
        }
    }
}
