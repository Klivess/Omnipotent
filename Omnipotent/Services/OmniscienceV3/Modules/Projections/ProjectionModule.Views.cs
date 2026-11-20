using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Projections
{
    public sealed partial class ProjectionModule
    {
        [Api.ApiContract]
        public sealed record NameSpanDto(string Kind, string Value, string Platform, long FromMs, long? ToMs);
        [Api.ApiContract]
        public sealed record IdentitySummaryDto(string IdentityId, string Platform, string Kind, long FirstSeenMs, long LastSeenMs);
        [Api.ApiContract]
        public sealed record PersonActivityDto(long? FirstMs, long? LastMs, long Msgs, long Msgs30, long Msgs90, long Msgs365, int ActiveDays, int ActiveDays30, double PerActiveDay,
            long Deleted, long Edited, long Replies, long Attachments, long Links, double DmShare, long ReactionsGiven, long MentionsMade);
        [Api.ApiContract]
        public sealed record DayCountDto(string Day, int Msgs, int Authored);
        [Api.ApiContract]
        public sealed record RankedCountDto(string Id, string Name, long Count);
        [Api.ApiContract]
        public sealed record PartnerSummaryDto(string PersonId, string Name, int RepliesOut, int RepliesIn, int MentionsOut, int MentionsIn, int DmOut, int DmIn, int ReactionsOut,
            int ReactionsIn, int ActiveDays, double Reciprocity, int? MedianReplyBucketOut, int? MedianReplyBucketIn);
        [Api.ApiContract]
        public sealed record LabelMixDto(Dictionary<string, Dictionary<string, double>> Recent, Dictionary<string, Dictionary<string, double>> Baseline,
            Dictionary<string, int> RecentAnswered, int? Labelset);
        [Api.ApiContract]
        public sealed record DossierFactDto(string AssertionId, string Attribute, string? Value, string Status, double? Probability, long? ValidFromEarliestMs,
            long? ValidFromLatestMs, long? ValidToEarliestMs, long? ValidToLatestMs, long? AssertedAtMs, long RecordedAtMs, string? Modality, string? SourceKind);
        [Api.ApiContract]
        public sealed record ZoneReadingDto(string? Zone, int? OffsetMinutes, double Confidence, string Method, string Month, IReadOnlyList<string> Alternatives);
        [Api.ApiContract]
        public sealed record DossierCoverageDto(IReadOnlyList<string> Platforms, int Conversations, int Guilds, long? FirstMs, long? LastMs);
        [Api.ApiContract]
        public sealed record DossierDto(string PersonId, string DisplayName, bool IsSelf, string Tier, bool Pinned, string AgeBand, bool Restricted,
            IReadOnlyList<NameSpanDto> Names, IReadOnlyList<IdentitySummaryDto> Identities, PersonActivityDto Activity, IReadOnlyList<DayCountDto> Series, int[]? Hours, int[]? Weekdays,
            ZoneReadingDto? Zone, IReadOnlyList<ZoneReadingDto>? ZoneHistory, LabelMixDto Labels, IReadOnlyList<RankedCountDto> Languages, IReadOnlyList<RankedCountDto> Conversations,
            IReadOnlyList<RankedCountDto> Guilds, IReadOnlyList<PartnerSummaryDto> Partners, IReadOnlyList<DossierFactDto> Facts, DossierCoverageDto Coverage, string Today, long NewestEvidenceMs,
            bool StaleEvidence, bool AnalysisOff = false);
        [Api.ApiContract]
        public sealed record PersonCardDto(string PersonId, string DisplayName, bool IsSelf, string Tier, bool Pinned, string AgeBand, IReadOnlyList<string> Platforms,
            long? FirstSeenMs, long? LastSeenMs, long Msgs, long Msgs30, long Msgs90, string? TopGuild, Dictionary<string, double>? Sentiment90);

        private const int TopN = 15;
        private const int FactLimit = 30;

        /// <summary>Builds one person's dossier and directory row (or removes them when the person was merged away or erased).</summary>
        private async Task ProjectPersonAsync(string personId, CancellationToken ct)
        {
            string person = Context.Policy.Canonical(personId);
            long now = Context.Clock.NowMs;
            string today = Today();
            bool exists;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                exists = corpus.Scalar<long>(null, "SELECT COUNT(*) FROM persons WHERE person_id=$p", ("$p", person)) > 0;
            // A merged-away id loses its own views (the survivor is projected under its canonical id); an erased person loses both.
            foreach (var gone in new[] { personId != person ? personId : null, exists ? null : person }.OfType<string>().Distinct())
            {
                await Context.ReadModels.DeleteAsync(Dossier, gone, ct).ConfigureAwait(false);
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM projection_people WHERE person_id=$p", ("$p", gone)), ct).ConfigureAwait(false);
            }
            if (!exists) return;

            var band = Context.Policy.BandOf(person, now);
            bool restricted = MinorGuard.IsRestricted(band);
            string tier = "archive";
            bool pinned = false;
            using (var op = Context.Stores.Operator.OpenRead())
            {
                var s = op.Query(null, "SELECT tier, pinned FROM person_settings WHERE person_id=$p", r => (r.GetString(0), r.GetInt64(1) == 1), ("$p", person)).FirstOrDefault();
                if (s != default) { tier = s.Item1; pinned = s.Item2; }
            }

            string displayName;
            bool isSelf;
            List<NameSpanDto> names;
            List<IdentitySummaryDto> identities;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                displayName = DisplayName(corpus, person);
                isSelf = corpus.Scalar<long>(null, """
                    SELECT COUNT(*) FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id WHERE COALESCE(pc.canonical_id, p.person_id)=$p AND p.is_self=1
                    """, ("$p", person)) > 0;
                identities = corpus.Query(null, """
                    SELECT i.identity_id, i.platform, i.kind, i.first_seen_ms, i.last_seen_ms FROM identities i LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                    WHERE COALESCE(pc.canonical_id, i.person_id)=$p ORDER BY i.first_seen_ms
                    """, r => new IdentitySummaryDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)), ("$p", person));
                names = identities.SelectMany(i => corpus.Query(null, "SELECT name_kind, value, valid_from_ms, valid_to_ms FROM identity_names WHERE identity_id=$i ORDER BY valid_from_ms",
                    r => new NameSpanDto(r.GetString(0), r.GetString(1), i.Platform, r.GetInt64(2), r.GetInt64OrNull(3)), ("$i", i.IdentityId))).Take(100).ToList();
            }

            List<PersonDayRow> days;
            List<PairTotals> pairs;
            List<ZoneMonth> zones;
            int? labelset;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                days = RollupReader.PersonDays(intel, person);
                pairs = RollupReader.Pairs(intel, person, DaysBefore(today, 90), today);
                zones = RollupReader.Zones(intel, person);
                labelset = ClassificationReader.ActiveLabelset(intel);
            }
            string d30 = DaysBefore(today, 29), d90 = DaysBefore(today, 89), d365 = DaysBefore(today, 364);
            bool In(string day, string from) => string.CompareOrdinal(day, from) >= 0 && string.CompareOrdinal(day, today) <= 0;
            long total = days.Sum(d => (long)d.Msgs);
            var activity = new PersonActivityDto(days.Where(d => d.FirstMs != null).Select(d => d.FirstMs).Min(), days.Where(d => d.LastMs != null).Select(d => d.LastMs).Max(),
                total, days.Where(d => In(d.Day, d30)).Sum(d => (long)d.Msgs), days.Where(d => In(d.Day, d90)).Sum(d => (long)d.Msgs), days.Where(d => In(d.Day, d365)).Sum(d => (long)d.Msgs),
                days.Count(d => d.Msgs > 0), days.Count(d => d.Msgs > 0 && In(d.Day, d30)), days.Count(d => d.Msgs > 0) == 0 ? 0 : Math.Round((double)total / days.Count(d => d.Msgs > 0), 2),
                days.Sum(d => (long)d.Deleted), days.Sum(d => (long)d.Edited), days.Sum(d => (long)d.Replies), days.Sum(d => (long)d.WithAttachments), days.Sum(d => (long)d.WithLinks),
                total == 0 ? 0 : Math.Round(days.Sum(d => (double)d.DmMsgs) / total, 4), days.Sum(d => (long)d.ReactionsGiven), days.Sum(d => (long)d.MentionsMade));
            var byDay = days.ToDictionary(d => d.Day, StringComparer.Ordinal);
            var series = Enumerable.Range(0, SeriesDays).Select(i => DaysBefore(today, SeriesDays - 1 - i))
                .Select(d => byDay.TryGetValue(d, out var row) ? new DayCountDto(d, row.Msgs, row.Authored) : new DayCountDto(d, 0, 0)).ToList();

            // Routine patterns (hours, weekdays, zone) are left out for minors (MinorGuard: no routine inference).
            int[]? hours = null, weekdays = null;
            if (!restricted)
            {
                hours = new int[24];
                weekdays = new int[7];
                foreach (var d in days.Where(d => In(d.Day, d365)))
                {
                    for (int h = 0; h < 24; h++) hours[h] += d.Hours[h];
                    weekdays[((int)RollupModule.ParseDay(d.Day).DayOfWeek + 6) % 7] += d.Msgs - d.DayOnly;
                }
            }
            ZoneReadingDto? zone = null;
            List<ZoneReadingDto>? zoneHistory = null;
            if (!restricted && zones.Count > 0)
            {
                zoneHistory = zones.TakeLast(24).Select(z => new ZoneReadingDto(z.Zone, z.OffsetMinutes, z.Confidence, z.Method, z.Month, z.Alternatives)).ToList();
                zone = zoneHistory.LastOrDefault(z => z.Zone != null) ?? zoneHistory[^1];
            }

            var recentLabels = RollupReader.SumLabels(days.Where(d => In(d.Day, d90)).Select(d => (d.Labels, d.Labelset)), labelset);
            var baseline = RollupReader.SumLabels(days.Select(d => (d.Labels, d.Labelset)), labelset);
            var labels = new LabelMixDto(Shares(recentLabels), Shares(baseline), recentLabels.ToDictionary(t => t.Key, t => t.Value.Values.Sum()), labelset);
            var languages = (baseline.GetValueOrDefault("language") ?? new Dictionary<string, int>())
                .OrderByDescending(kv => kv.Value).Take(8).Select(kv => new RankedCountDto(kv.Key, kv.Key, kv.Value)).ToList();

            var convCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            var guildCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in days.Where(d => In(d.Day, d90)))
            {
                foreach (var (k, v) in d.Conversations) convCounts[k] = convCounts.GetValueOrDefault(k) + v;
                foreach (var (k, v) in d.Guilds) guildCounts[k] = guildCounts.GetValueOrDefault(k) + v;
            }
            List<RankedCountDto> conversations, guilds;
            List<PartnerSummaryDto> partners;
            var platforms = identities.Select(i => i.Platform).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
            int allConversations, allGuilds;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                conversations = convCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(TopN)
                    .Select(kv => new RankedCountDto(kv.Key, ConversationTitle(corpus, kv.Key), kv.Value)).ToList();
                guilds = guildCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(TopN)
                    .Select(kv => new RankedCountDto(kv.Key, GuildName(corpus, kv.Key), kv.Value)).ToList();
                partners = pairs.Take(TopN).Select(p => new PartnerSummaryDto(p.Partner, DisplayName(corpus, p.Partner), p.RepliesOut, p.RepliesIn, p.MentionsOut, p.MentionsIn,
                    p.DmOut, p.DmIn, p.ReactionsOut, p.ReactionsIn, p.ActiveDays, Reciprocity(p), MedianBucket(p.LatencyOut), MedianBucket(p.LatencyIn))).ToList();
                var allConv = days.SelectMany(d => d.Conversations.Keys).ToHashSet(StringComparer.Ordinal);
                allConversations = allConv.Count;
                allGuilds = days.SelectMany(d => d.Guilds.Keys).Distinct().Count();
            }
            var facts = Facts(person, band);
            long newest = activity.LastMs ?? 0;
            // Plan "the person brief carries a staleness banner when the newest evidence is old": 90 days.
            bool stale = newest > 0 && now - newest > 90L * 86_400_000;
            var dossier = new DossierDto(person, displayName, isSelf, tier, pinned, band.ToString(), restricted, names, identities, activity, series, hours, weekdays,
                zone, zoneHistory, labels, languages, conversations, guilds, partners, facts,
                new DossierCoverageDto(platforms, allConversations, allGuilds, activity.FirstMs, activity.LastMs), today, newest, stale,
                // Analysis switched off (N20): the page says so, instead of looking like a person nothing is known about.
                AnalysisOff: Storage.AnalysisOff.IsOff(Context.Stores, person));
            long seq;
            using (var cr = Context.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(cr);
            await Context.ReadModels.PutAsync(Dossier, person, 1, now, seq, dossier, ct).ConfigureAwait(false);

            var sentiment = labels.Recent.GetValueOrDefault("sentiment");
            var card = new PersonCardDto(person, displayName, isSelf, tier, pinned, band.ToString(), platforms, activity.FirstMs, activity.LastMs, activity.Msgs, activity.Msgs30,
                activity.Msgs90, guilds.FirstOrDefault()?.Name, sentiment);
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO projection_people(person_id, display_name, sort_name, tier, pinned, band, is_self, platforms, identities, first_seen_ms, last_seen_ms,
                    msgs_total, msgs_30d, msgs_90d, card_json, as_of_ms, source_seq)
                VALUES($p,$n,$s,$t,$pin,$b,$self,$pl,$i,$f,$l,$tot,$m30,$m90,$card,$a,$q)
                ON CONFLICT(person_id) DO UPDATE SET display_name=excluded.display_name, sort_name=excluded.sort_name, tier=excluded.tier, pinned=excluded.pinned,
                    band=excluded.band, is_self=excluded.is_self, platforms=excluded.platforms, identities=excluded.identities, first_seen_ms=excluded.first_seen_ms,
                    last_seen_ms=excluded.last_seen_ms, msgs_total=excluded.msgs_total, msgs_30d=excluded.msgs_30d, msgs_90d=excluded.msgs_90d, card_json=excluded.card_json,
                    as_of_ms=excluded.as_of_ms, source_seq=excluded.source_seq
                """, ("$p", person), ("$n", displayName), ("$s", SortName(displayName)), ("$t", tier), ("$pin", pinned ? 1 : 0), ("$b", band.ToString()), ("$self", isSelf ? 1 : 0),
                ("$pl", string.Join(",", platforms)), ("$i", identities.Count), ("$f", activity.FirstMs ?? 0), ("$l", activity.LastMs ?? 0), ("$tot", activity.Msgs),
                ("$m30", activity.Msgs30), ("$m90", activity.Msgs90), ("$card", JsonSerializer.Serialize(card, Kernel.Jobs.KernelJson.Options)), ("$a", now), ("$q", seq)), ct).ConfigureAwait(false);
        }

        /// <summary>Case- and accent-folded name for ordering and prefix search.</summary>
        public static string SortName(string name)
        {
            string d = name.Normalize(System.Text.NormalizationForm.FormKD);
            var sb = new System.Text.StringBuilder(d.Length);
            foreach (char ch in d)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString().Trim();
        }

        /// <summary>How evenly the exchange goes both ways: 1 = balanced, 0 = one-way.</summary>
        private static double Reciprocity(PairTotals p)
        {
            double outgoing = p.RepliesOut + p.MentionsOut + p.DmOut + p.ReactionsOut, incoming = p.RepliesIn + p.MentionsIn + p.DmIn + p.ReactionsIn;
            return outgoing + incoming == 0 ? 0 : Math.Round(1 - Math.Abs(outgoing - incoming) / (outgoing + incoming), 3);
        }

        private static int? MedianBucket(int[] buckets)
        {
            int total = buckets.Sum();
            if (total == 0) return null;
            int seen = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                seen += buckets[i];
                if (seen * 2 >= total) return i;
            }
            return buckets.Length - 1;
        }

        /// <summary>Current facts from the knowledge store, filtered by MinorGuard and the sensitive-trait rule (never inferred).</summary>
        private List<DossierFactDto> Facts(string person, AgeBand band)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, """
                SELECT assertion_id, attribute, value_text, status, probability, valid_from_earliest_ms, valid_from_latest_ms, valid_to_earliest_ms, valid_to_latest_ms,
                       asserted_at_ms, recorded_at_ms, modality, source_kind
                FROM assertions WHERE subject_kind='person' AND subject_id=$p AND superseded_at_ms IS NULL AND retracted_at_ms IS NULL AND status <> 'rejected'
                ORDER BY recorded_at_ms DESC LIMIT 200
                """, r => new DossierFactDto(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetDoubleOrNull(4), r.GetInt64OrNull(5), r.GetInt64OrNull(6),
                    r.GetInt64OrNull(7), r.GetInt64OrNull(8), r.GetInt64OrNull(9), r.GetInt64(10), r.GetStringOrNull(11), r.GetStringOrNull(12)), ("$p", person))
                .Where(f => MinorGuard.AllowsAttribute(f.Attribute, band) && SensitiveTraits.AllowsAssertion(f.Attribute, f.SourceKind, f.Modality))
                .Take(FactLimit).ToList();
        }

        private static string ConversationTitle(SqliteConnection corpus, string id) =>
            corpus.Scalar<string>(null, "SELECT name FROM conversation_names WHERE conversation_id=$i AND valid_to_ms IS NULL ORDER BY valid_from_ms DESC LIMIT 1", ("$i", id))
            ?? corpus.Scalar<string>(null, "SELECT title FROM conversations WHERE conversation_id=$i", ("$i", id)) ?? id;

        private static string GuildName(SqliteConnection corpus, string id) =>
            corpus.Scalar<string>(null, "SELECT name FROM guild_names WHERE guild_id=$i AND valid_to_ms IS NULL ORDER BY valid_from_ms DESC LIMIT 1", ("$i", id))
            ?? corpus.Scalar<string>(null, "SELECT name FROM guilds WHERE guild_id=$i", ("$i", id)) ?? id;

        // ── conversation and server cards ────────────────────────────────────────

        [Api.ApiContract]

        public sealed record ScopeCardDto(string Id, string Kind, string Title, string? Platform, string? GuildId, string? GuildName, IReadOnlyList<NameSpanDto> NameHistory,
            long Msgs, long Msgs30, long Msgs90, long? FirstMs, long? LastMs, IReadOnlyList<DayCountDto> Series, IReadOnlyList<RankedCountDto> TopPeople90, int People90,
            Dictionary<string, Dictionary<string, double>> Labels90, IReadOnlyList<RankedCountDto>? Channels, int? Labelset, string Today);

        private async Task ProjectConversationAsync(string id, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            string today = Today();
            (string Kind, string Platform, string? Guild, string Title)? meta;
            List<NameSpanDto> history;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                meta = corpus.Query(null, "SELECT kind, platform, guild_id FROM conversations WHERE conversation_id=$c", r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), ""), ("$c", id)).FirstOrDefault();
                if (meta == default((string, string, string?, string))) { await Context.ReadModels.DeleteAsync(ConversationCard, id, ct).ConfigureAwait(false); return; }
                meta = meta.Value with { Title = ConversationTitle(corpus, id) };
                history = corpus.Query(null, "SELECT name, valid_from_ms, valid_to_ms FROM conversation_names WHERE conversation_id=$c ORDER BY valid_from_ms",
                    r => new NameSpanDto("title", r.GetString(0), meta.Value.Platform, r.GetInt64(1), r.GetInt64OrNull(2)), ("$c", id));
            }
            var card = await BuildScopeCardAsync(RollupModule.Cells.Conversation, id, meta.Value.Kind, meta.Value.Title, meta.Value.Platform, meta.Value.Guild, history, today, null).ConfigureAwait(false);
            await Context.ReadModels.PutAsync(ConversationCard, id, 1, now, card.Seq, card.Card, ct).ConfigureAwait(false);
        }

        private async Task ProjectGuildAsync(string id, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            string today = Today();
            string? platform;
            List<NameSpanDto> history;
            List<string> channels;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                platform = corpus.Scalar<string>(null, "SELECT platform FROM guilds WHERE guild_id=$g", ("$g", id));
                if (platform == null) { await Context.ReadModels.DeleteAsync(GuildCard, id, ct).ConfigureAwait(false); return; }
                history = corpus.Query(null, "SELECT name, valid_from_ms, valid_to_ms FROM guild_names WHERE guild_id=$g ORDER BY valid_from_ms",
                    r => new NameSpanDto("name", r.GetString(0), platform, r.GetInt64(1), r.GetInt64OrNull(2)), ("$g", id));
                channels = corpus.Query(null, "SELECT conversation_id FROM conversations WHERE guild_id=$g", r => r.GetString(0), ("$g", id));
            }
            string title;
            using (var corpus = Context.Stores.Corpus.OpenRead()) title = GuildName(corpus, id);
            var card = await BuildScopeCardAsync(RollupModule.Cells.Guild, id, "guild", title, platform, id, history, today, channels).ConfigureAwait(false);
            await Context.ReadModels.PutAsync(GuildCard, id, 1, now, card.Seq, card.Card, ct).ConfigureAwait(false);
        }

        private Task<(ScopeCardDto Card, long Seq)> BuildScopeCardAsync(string scope, string id, string kind, string title, string? platform, string? guild,
            List<NameSpanDto> history, string today, List<string>? channels)
        {
            List<ScopeDayRow> days;
            int? labelset;
            var channelCounts = new List<RankedCountDto>();
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                days = RollupReader.ScopeDays(intel, scope, id);
                labelset = ClassificationReader.ActiveLabelset(intel);
                if (channels != null)
                {
                    foreach (var ch in channels)
                    {
                        long n = intel.Scalar<long?>(null, "SELECT SUM(msgs) FROM rollup_scope_day WHERE scope='conversation' AND key=$k AND day >= $a", ("$k", ch), ("$a", DaysBefore(today, 89))) ?? 0;
                        channelCounts.Add(new RankedCountDto(ch, "", n));
                    }
                }
            }
            string d30 = DaysBefore(today, 29), d90 = DaysBefore(today, 89);
            bool In(string day, string from) => string.CompareOrdinal(day, from) >= 0 && string.CompareOrdinal(day, today) <= 0;
            var people = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in days.Where(d => In(d.Day, d90)))
                foreach (var (p, n) in d.Persons) people[p] = people.GetValueOrDefault(p) + n;
            var byDay = days.ToDictionary(d => d.Day, StringComparer.Ordinal);
            var series = Enumerable.Range(0, 90).Select(i => DaysBefore(today, 89 - i))
                .Select(d => byDay.TryGetValue(d, out var r) ? new DayCountDto(d, r.Msgs, r.Authored) : new DayCountDto(d, 0, 0)).ToList();
            List<RankedCountDto> top;
            string? guildName = null;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                top = people.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(TopN).Select(kv => new RankedCountDto(kv.Key, DisplayName(corpus, kv.Key), kv.Value)).ToList();
                if (guild != null) guildName = GuildName(corpus, guild);
                channelCounts = channelCounts.OrderByDescending(c => c.Count).ThenBy(c => c.Id, StringComparer.Ordinal).Take(50)
                    .Select(c => c with { Name = ConversationTitle(corpus, c.Id) }).ToList();
            }
            var labels90 = RollupReader.SumLabels(days.Where(d => In(d.Day, d90)).Select(d => (d.Labels, d.Labelset)), labelset);
            var card = new ScopeCardDto(id, kind, title, platform, guild, guildName, history, days.Sum(d => (long)d.Msgs), days.Where(d => In(d.Day, d30)).Sum(d => (long)d.Msgs),
                days.Where(d => In(d.Day, d90)).Sum(d => (long)d.Msgs), days.Select(d => d.FirstMs).Where(x => x != null).Min(), days.Select(d => d.LastMs).Where(x => x != null).Max(),
                series, top, people.Count, Shares(labels90), channels == null ? null : channelCounts, labelset, today);
            long seq;
            using (var cr = Context.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(cr);
            return Task.FromResult((card, seq));
        }

        // ── overview and engine health ───────────────────────────────────────────

        [Api.ApiContract]

        public sealed record CorpusTotalsDto(long Messages, long People, long Identities, long Conversations, long Guilds, long Attachments, long Sources);
        [Api.ApiContract]
        public sealed record OverviewDayDto(string Day, int Msgs, int People);
        [Api.ApiContract]
        public sealed record SourceStatusDto(string SourceId, string Platform, string Kind, string? Label, string Status, string? Detail, long UpdatedAtMs);
        [Api.ApiContract]
        public sealed record OverviewDto(CorpusTotalsDto Totals, IReadOnlyList<OverviewDayDto> Series, Dictionary<string, Dictionary<string, double>> Labels30, IReadOnlyList<RankedCountDto> TopPeople30,
            IReadOnlyList<RankedCountDto> TopGuilds30, IReadOnlyList<RankedCountDto> TopConversations30, IReadOnlyList<SourceStatusDto> Sources, Dictionary<string, long> Platforms,
            long? FirstMs, long? LastMs, int? Labelset, string Today, long CorpusSeq);

        internal async Task ProjectOverviewAsync(CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            string today = Today(), d30 = DaysBefore(today, 29), d90 = DaysBefore(today, 89);
            CorpusTotalsDto totals;
            List<SourceStatusDto> sources;
            long seq;
            Dictionary<string, long> platforms;
            long? first, last;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                totals = new CorpusTotalsDto(messageCount.Read(corpus, now),
                    corpus.Scalar<long>(null, "SELECT COUNT(*) FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id WHERE COALESCE(pc.canonical_id,p.person_id)=p.person_id"),
                    corpus.Scalar<long>(null, "SELECT COUNT(*) FROM identities"), corpus.Scalar<long>(null, "SELECT COUNT(*) FROM conversations"),
                    corpus.Scalar<long>(null, "SELECT COUNT(*) FROM guilds"), attachmentCount.Read(corpus, now),
                    corpus.Scalar<long>(null, "SELECT COUNT(*) FROM sources"));
                sources = corpus.Query(null, "SELECT source_id, platform, kind, label, status, status_detail, updated_at_ms FROM sources ORDER BY source_id",
                    r => new SourceStatusDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetString(4), r.GetStringOrNull(5), r.GetInt64(6)));
                seq = ChangeLog.MaxSeq(corpus);
                first = corpus.Scalar<long?>(null, "SELECT MIN(sent_at_ms) FROM messages");
                last = corpus.Scalar<long?>(null, "SELECT MAX(sent_at_ms) FROM messages");
            }
            List<ScopeDayRow> global;
            List<(string Kind, string Key, long Msgs)> top;
            int? labelset;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                global = RollupReader.ScopeDays(intel, RollupModule.Cells.Global, "all", d90, today);
                labelset = ClassificationReader.ActiveLabelset(intel);
                top = intel.Query(null, """
                    SELECT scope, key, SUM(msgs) FROM rollup_scope_day WHERE scope IN ('guild','conversation') AND day >= $a AND day <= $b GROUP BY scope, key
                    """, r => (r.GetString(0), r.GetString(1), r.GetInt64(2)), ("$a", d30), ("$b", today));
            }
            var people30 = new Dictionary<string, long>(StringComparer.Ordinal);
            platforms = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in global.Where(d => string.CompareOrdinal(d.Day, d30) >= 0))
                foreach (var (p, n) in d.Persons) people30[p] = people30.GetValueOrDefault(p) + n;
            foreach (var d in global)
                foreach (var (p, n) in d.Platforms) platforms[p] = platforms.GetValueOrDefault(p) + n;
            var byDay = global.ToDictionary(d => d.Day, StringComparer.Ordinal);
            var series = Enumerable.Range(0, 90).Select(i => DaysBefore(today, 89 - i))
                .Select(d => byDay.TryGetValue(d, out var r) ? new OverviewDayDto(d, r.Msgs, r.People) : new OverviewDayDto(d, 0, 0)).ToList();
            List<RankedCountDto> topPeople, topGuilds, topConversations;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                topPeople = people30.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(10).Select(kv => new RankedCountDto(kv.Key, DisplayName(corpus, kv.Key), kv.Value)).ToList();
                topGuilds = top.Where(t => t.Kind == "guild").OrderByDescending(t => t.Msgs).ThenBy(t => t.Key, StringComparer.Ordinal).Take(10).Select(t => new RankedCountDto(t.Key, GuildName(corpus, t.Key), t.Msgs)).ToList();
                topConversations = top.Where(t => t.Kind == "conversation").OrderByDescending(t => t.Msgs).ThenBy(t => t.Key, StringComparer.Ordinal).Take(10)
                    .Select(t => new RankedCountDto(t.Key, ConversationTitle(corpus, t.Key), t.Msgs)).ToList();
            }
            var labels30 = RollupReader.SumLabels(global.Where(d => string.CompareOrdinal(d.Day, d30) >= 0).Select(d => (d.Labels, d.Labelset)), labelset);
            var dto = new OverviewDto(totals, series, Shares(labels30), topPeople, topGuilds, topConversations, sources, platforms, first, last, labelset, today, seq);
            await Context.ReadModels.PutAsync(Overview, "global", 1, now, seq, dto, ct).ConfigureAwait(false);
        }

        [Api.ApiContract]

        public sealed record ModuleBacklogDto(string ModuleId, bool Enabled, long Watermark, long Backlog, long Dirty, long Queued, long Leased, long Dead, long? LastSucceededMs, string? LastError);
        [Api.ApiContract]
        public sealed record StoreSizeDto(string Name, long FileBytes, long WalBytes, long PendingWrites);
        [Api.ApiContract]
        public sealed record EngineHealthDto(IReadOnlyList<ModuleBacklogDto> Modules, IReadOnlyList<StoreSizeDto> Stores, Dictionary<string, long> JobsByState, long LlmCallsHour,
            long LlmFailuresHour, double LlmCacheEfficiencyHour, object? Encoder, long MessageVectors, long RollupDirty, long CorpusSeq, IReadOnlyList<string> Problems);

        /// <summary>
        /// The number of rows in a table that only grows (messages, attachments), without counting it all each time. The
        /// overview is rebuilt every five minutes, and counting 6.3 million messages for each rebuild read the table's whole
        /// index 288 times a day. The whole table is counted once; after that only the rows added since are (rows are added at
        /// the end, so they are the ones past the last row seen). Rows that were removed in between (a person forgotten) are
        /// caught by counting it all again every few hours.
        /// </summary>
        internal sealed class GrowingCount
        {
            private readonly string table;
            private long count = -1, lastRow, countedAtMs;
            internal static long RecountEveryMs { get; set; } = 6 * 3_600_000L;

            public GrowingCount(string table) => this.table = table;

            public long Read(SqliteConnection c, long nowMs)
            {
                lock (this)
                {
                    long top = c.Scalar<long?>(null, $"SELECT MAX(rowid) FROM {table}") ?? 0;
                    if (count < 0 || top < lastRow || nowMs - countedAtMs >= RecountEveryMs || nowMs < countedAtMs)
                    {
                        count = c.Scalar<long>(null, $"SELECT COUNT(*) FROM {table}");
                        countedAtMs = nowMs;
                    }
                    else if (top > lastRow) count += c.Scalar<long>(null, $"SELECT COUNT(*) FROM {table} WHERE rowid > $r", ("$r", lastRow));
                    lastRow = top;
                    return count;
                }
            }
        }

        private readonly GrowingCount messageCount = new("messages"), attachmentCount = new("attachments");

        /// <summary>The data-health view (F556) and its problems list, which the X51 monitor turns into alerts.</summary>
        internal async Task ProjectHealthAsync(CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            long seq;
            using (var corpus = Context.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(corpus);
            List<ModuleBacklogDto> modules;
            long rollupDirty;
            // The job figures come from the counters the queue's triggers keep. This view is rebuilt every minute, and it
            // used to count each module's jobs and read every finished one for its latest time: with a million jobs in the
            // table that is the same reading the status page was freed of, done here 1,440 times a day, and on the loaded
            // server one rebuild went two hours without finishing (20 Nov 2026).
            var stats = Context.Jobs.ModuleStats();
            var jobs = Context.Jobs.Counts().GroupBy(c => c.State, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(c => c.Count), StringComparer.Ordinal);
            using (var c = Context.Stores.Intel.OpenRead())
            {
                modules = c.Query(null, """
                    SELECT s.module_id, s.enabled, s.watermark_seq, (SELECT COUNT(*) FROM module_dirty d WHERE d.module_id=s.module_id)
                    FROM module_state s ORDER BY s.module_id
                    """, r => (Id: r.GetString(0), Enabled: r.GetInt64(1) == 1, Watermark: r.GetInt64(2), Dirty: r.GetInt64(3)))
                    .Select(m =>
                    {
                        var s = stats.GetValueOrDefault(m.Id) ?? new Kernel.Jobs.ModuleJobStats();
                        return new ModuleBacklogDto(m.Id, m.Enabled, m.Watermark, Math.Max(0, seq - m.Watermark), m.Dirty, s.Queued, s.Leased, s.Dead, s.LastSucceededMs,
                            s.Dead > 0 ? s.LastError : null);
                    }).ToList();
                rollupDirty = c.Scalar<long>(null, "SELECT COUNT(*) FROM rollup_dirty");
            }
            var stores = Context.Stores.All.Select(s => s.GetHealth()).Select(h => new StoreSizeDto(h.Name, h.FileBytes, h.WalBytes, h.PendingWrites)).ToList();
            long calls = 0, failures = 0;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                calls = c.Scalar<long>(null, "SELECT COUNT(*) FROM llm_calls WHERE at_ms >= $t", ("$t", now - 3_600_000));
                failures = c.Scalar<long>(null, "SELECT COUNT(*) FROM llm_calls WHERE at_ms >= $t AND outcome <> 'ok'", ("$t", now - 3_600_000));
            }
            double efficiency = Context.Llm?.CacheEfficiency(TimeSpan.FromHours(1)).Efficiency ?? 1.0;
            var encoder = Context.Encoder;
            long vectors = encoder == null || Context.Vectors == null ? 0 : Context.Vectors.Count(VectorSpaces.Message, encoder.ModelVersion);
            var problems = new List<string>();
            foreach (var m in modules.Where(m => m.Dead > 0)) problems.Add($"{m.ModuleId}: {m.Dead} dead-lettered job(s){(m.LastError == null ? "" : ": " + m.LastError)}");
            foreach (var m in modules.Where(m => m.Enabled && m.Backlog > 100_000)) problems.Add($"{m.ModuleId}: {m.Backlog:N0} changes behind");
            if (calls >= 10 && failures * 2 > calls) problems.Add($"LLM: {failures} of {calls} calls failed in the last hour");
            if (calls >= 20 && efficiency < 0.6) problems.Add($"LLM: prefix-cache efficiency {efficiency:P0} in the last hour (floor 60%)");
            if (encoder == null) problems.Add("No shared encoder: classification and semantic search are off");
            foreach (var s in stores.Where(s => s.WalBytes > 512L * 1024 * 1024)) problems.Add($"{s.Name}: WAL is {s.WalBytes / (1024 * 1024)} MB (checkpoints are starved)");
            var dto = new EngineHealthDto(modules, stores, jobs, calls, failures, Math.Round(efficiency, 4), encoder is IEncoderDiagnostics d ? d.Stats() : null, vectors, rollupDirty, seq, problems);
            await Context.ReadModels.PutAsync(Health, "global", 1, now, seq, dto, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-applies MinorGuard to a served dossier (defence in depth): if the person's band became restricted after the dossier
    /// was built (Klives marked them, new age evidence), routine sections and restricted facts are removed before serving.
    /// </summary>
    public sealed class DossierServingFilter : IReadModelServingFilter
    {
        public bool Handles(string modelId) => modelId == ProjectionModule.Dossier;

        public string Filter(ReadModelRow row, IPolicyOracle policy, long nowMs)
        {
            var band = policy.BandOf(row.Key, nowMs);
            if (!MinorGuard.IsRestricted(band)) return row.BodyJson;
            var node = JsonNode.Parse(row.BodyJson)!.AsObject();
            node["restricted"] = true;
            node["ageBand"] = band.ToString();
            foreach (var key in new[] { "hours", "weekdays", "zone", "zoneHistory" }) node.Remove(key);
            if (node["facts"] is JsonArray facts)
            {
                var keep = new JsonArray();
                foreach (var f in facts)
                    if (f?["attribute"]?.GetValue<string>() is { } attr && MinorGuard.AllowsAttribute(attr, band)) keep.Add(f.DeepClone());
                node["facts"] = keep;
            }
            return node.ToJsonString();
        }
    }
}
