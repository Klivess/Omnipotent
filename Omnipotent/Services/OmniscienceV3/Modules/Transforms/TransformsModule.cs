using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Transforms
{
    /// <summary>One pinned item of an evidence bundle.</summary>
    [Api.ApiContract]
    public sealed record BundleItemDto(string Kind, string Id, string? Label);

    /// <summary>A case file: pinned items and Klives's note (X38). <c>format</c> of an export is markdown or json.</summary>
    [Api.ApiContract]
    public sealed record BundleDto(string BundleId, string Title, string? Note, IReadOnlyList<BundleItemDto> Items, string CreatedBy, long CreatedAtMs, long UpdatedAtMs);

    [Api.ApiContract]
    public sealed record BundleListDto(IReadOnlyList<BundleDto> Bundles);

    /// <summary>
    /// The transform tactics that need no model (plan "Selection → derived data"): find similar (X20), context (X21),
    /// phrase tracker (X22), cohort and period comparison (X28, X29), common ground (X34), closest contacts (X37), evidence
    /// bundle (X38), dataset export (X39) and custom chart (X40). The module owns no jobs and no intel tables: it reads the
    /// corpus, the vectors and the rollups, and keeps bundles and charts in operator.db (they are Klives's own).
    /// </summary>
    public sealed class TransformsModule : IntelModuleBase
    {
        public const string ModuleId = "transforms";
        /// <summary>operator.db <c>notes.subject_kind</c> of evidence bundles.</summary>
        public const string BundleKind = "bundle";
        /// <summary>operator.db <c>saved_views.page</c> of charts on the personal dashboard.</summary>
        public const string DashboardPage = "dashboard";

        private readonly Func<IEnumerable<IIntelModule>>? otherModules;

        /// <param name="otherModules">The modules of this build, so a saved chart can be checked against the datasets they declare.</param>
        public TransformsModule(Func<IEnumerable<IIntelModule>>? otherModules = null) => this.otherModules = otherModules;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Transforms",
            Category = "tactics",
            Description = "Tactics that turn a selection into derived data without a model: similar messages, context, phrase spread, comparisons, common ground, closest contacts, evidence bundles, exports and saved charts.",
        };

        internal ModuleContext Ctx => Context;

        internal IEnumerable<DatasetDescriptor> Datasets => (otherModules?.Invoke() ?? Array.Empty<IIntelModule>()).SelectMany(m => m.Manifest.Datasets);

        public override Task<string?> ExecuteAsync(JobContext job) => throw new JobFailureException(ErrorClass.Permanent, $"Transforms has no job kind '{job.Job.Kind}'.");

        public override IReadOnlyList<ITactic> Tactics => tactics ??= new ITactic[]
        {
            new FindSimilarTactic(this), new ContextExpanderTactic(this), new PhraseTrackerTactic(this), new CohortComparisonTactic(this), new PeriodComparisonTactic(this),
            new CommonGroundTactic(this), new ClosestContactsTactic(this), new EvidenceBundleTactic(this), new DatasetExportTactic(this), new CustomChartTactic(this),
        };

        private IReadOnlyList<ITactic>? tactics;

        // ── shared pieces ────────────────────────────────────────────────────────

        /// <summary>Where a tactic may look, from the selections beside its main one.</summary>
        internal sealed record Scope(HashSet<string> Persons, HashSet<string> Conversations, HashSet<string> Guilds, long? FromMs, long? ToMs)
        {
            public bool Any => Persons.Count + Conversations.Count + Guilds.Count > 0 || FromMs != null;

            public bool Matches(string? person, string conversation, string? guild, long atMs) =>
                (Persons.Count == 0 || (person != null && Persons.Contains(person)))
                && (Conversations.Count == 0 || Conversations.Contains(conversation))
                && (Guilds.Count == 0 || (guild != null && Guilds.Contains(guild)))
                && (FromMs == null || (atMs >= FromMs && atMs < ToMs));
        }

        internal static Scope ScopeOf(TacticRunContext context)
        {
            IEnumerable<string> Ids(SelectionKind kind) => context.Input.Selections.Where(s => s.Kind == kind).SelectMany(s => s.Items.Select(i => i.Id));
            long? from = null, to = null;
            foreach (var s in context.Input.Selections.Where(s => s.Kind == SelectionKind.TimeRange))
                if (s.Items.Count > 0 && Selection.TryParseTimeRange(s.Items[0].Id, out long a, out long b)) (from, to) = (a, b);
            return new Scope(Ids(SelectionKind.Person).Select(context.CanonicalPerson).ToHashSet(StringComparer.Ordinal), Ids(SelectionKind.Conversation).ToHashSet(StringComparer.Ordinal),
                Ids(SelectionKind.Guild).ToHashSet(StringComparer.Ordinal), from, to);
        }

        internal sealed record MessageInfo(string Id, string Conversation, string? Guild, string AuthorIdentity, string? Person, long AtMs, string? Text, string? ReplyTo, long? DeletedAt, bool Bot);

        internal const string InfoColumns = """
            m.message_id, m.conversation_id, m.guild_id, m.author_identity_id, COALESCE(pc.canonical_id, i.person_id), m.sent_at_ms, m.content, m.reply_to_message_id, m.deleted_at_ms,
            (m.is_bot=1 OR m.is_system=1 OR m.is_webhook=1 OR COALESCE(i.kind,'')='bot')
            FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
            """;

        internal static MessageInfo ReadInfo(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetStringOrNull(4), r.GetInt64(5),
            r.GetStringOrNull(6), r.GetStringOrNull(7), r.GetInt64OrNull(8), r.GetInt64(9) == 1);

        internal static Dictionary<string, string?> PersonNames(SqliteConnection corpus, IEnumerable<string?> persons) =>
            corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", persons.OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

        internal string Day(long ms) => Zones.LocalDate(ms, Context.Settings.KlivesZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        internal bool HasTable(string name)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", ("$n", name)) > 0;
        }

        internal bool Restricted(string person, long atMs) => MinorGuard.IsRestricted(Context.Policy.BandOf(person, atMs));

        // ── evidence bundles (X38) ───────────────────────────────────────────────

        private sealed record BundleBody(string Title, string? Note, List<BundleItemDto> Items, string CreatedBy);

        public IReadOnlyList<BundleDto> Bundles()
        {
            using var op = Context.Stores.Operator.OpenRead();
            return op.Query(null, "SELECT note_id, body, created_at_ms, updated_at_ms FROM notes WHERE subject_kind=$k AND deleted_at_ms IS NULL ORDER BY updated_at_ms DESC, note_id",
                r => ToBundle(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)), ("$k", BundleKind)).OfType<BundleDto>().ToList();
        }

        public BundleDto? Bundle(string bundleId)
        {
            using var op = Context.Stores.Operator.OpenRead();
            return op.Query(null, "SELECT note_id, body, created_at_ms, updated_at_ms FROM notes WHERE subject_kind=$k AND note_id=$i AND deleted_at_ms IS NULL",
                r => ToBundle(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)), ("$k", BundleKind), ("$i", bundleId)).FirstOrDefault();
        }

        private static BundleDto? ToBundle(string id, string body, long created, long updated)
        {
            try
            {
                var b = JsonSerializer.Deserialize<BundleBody>(body, KernelJson.Options);
                return b == null ? null : new BundleDto(id, b.Title, b.Note, b.Items, b.CreatedBy, created, updated);
            }
            catch (JsonException)
            {
                return null; // a note of this kind that is not a bundle is not listed
            }
        }

        /// <summary>Creates a bundle, or adds the items to an existing one (an item already in it is not added twice).</summary>
        public async Task<BundleDto> SaveBundleAsync(string? bundleId, string title, string? note, IReadOnlyList<BundleItemDto> items, string actor, CancellationToken ct)
        {
            title = (title ?? "").Trim();
            if (title.Length is < 1 or > 160) throw new ArgumentException("A bundle needs a title of 1 to 160 characters.");
            if (note is { Length: > 8000 }) throw new ArgumentException("A bundle's note can be at most 8000 characters.");
            long now = Context.Clock.NowMs;
            var existing = bundleId == null ? null : Bundle(bundleId) ?? throw new KeyNotFoundException($"No bundle '{bundleId}'.");
            var all = (existing?.Items ?? Array.Empty<BundleItemDto>()).Concat(items).GroupBy(i => i.Kind + "\u0001" + i.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            if (all.Count > 5000) throw new ArgumentException("A bundle can hold at most 5000 items.");
            string id = existing?.BundleId ?? "b" + Guid.NewGuid().ToString("N")[..12];
            string body = JsonSerializer.Serialize(new BundleBody(title, string.IsNullOrWhiteSpace(note) ? existing?.Note : note!.Trim(), all, existing?.CreatedBy ?? actor), KernelJson.Options);
            await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, """
                    INSERT INTO notes(note_id, subject_kind, subject_id, body, tags, created_at_ms, updated_at_ms) VALUES($i,$k,$i,$b,'bundle',$n,$n)
                    ON CONFLICT(note_id) DO UPDATE SET body=excluded.body, updated_at_ms=excluded.updated_at_ms
                    """, ("$i", id), ("$k", BundleKind), ("$b", body), ("$n", now));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,$x,$g,$d)", ("$t", now), ("$a", actor), ("$x", existing == null ? "bundle.create" : "bundle.update"),
                    ("$g", id), ("$d", JsonSerializer.Serialize(new { title, items = all.Count }, KernelJson.Options)));
            }, ct).ConfigureAwait(false);
            return Bundle(id)!;
        }

        public async Task<bool> DeleteBundleAsync(string bundleId, string actor, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            return await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                int n = c.Exec(tx, "UPDATE notes SET deleted_at_ms=$n, updated_at_ms=$n WHERE note_id=$i AND subject_kind=$k AND deleted_at_ms IS NULL", ("$n", now), ("$i", bundleId), ("$k", BundleKind));
                if (n > 0) c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'bundle.delete',$g,NULL)", ("$t", now), ("$a", actor), ("$g", bundleId));
                return n;
            }, ct).ConfigureAwait(false) > 0;
        }

        /// <summary>
        /// The bundle as a document, written from the corpus as it is now: each message with its T3 stamp, author and text;
        /// people by name; facts with the dossier's filters (a restricted or sensitive fact is listed as withheld, not shown).
        /// </summary>
        public string ExportBundle(string bundleId, string format)
        {
            var bundle = Bundle(bundleId) ?? throw new KeyNotFoundException($"No bundle '{bundleId}'.");
            format = (format ?? "markdown").Trim().ToLowerInvariant();
            if (format is not ("markdown" or "json")) throw new ArgumentException("'format' must be markdown or json.");
            long now = Context.Clock.NowMs;
            var messageIds = bundle.Items.Where(i => i.Kind == "message").Select(i => i.Id).ToList();
            var personIds = bundle.Items.Where(i => i.Kind == "person").Select(i => Context.Policy.Canonical(i.Id)).ToList();
            var conversationIds = bundle.Items.Where(i => i.Kind == "conversation").Select(i => i.Id).ToList();
            var assertionIds = bundle.Items.Where(i => i.Kind == "assertion").Select(i => i.Id).ToList();
            List<MessageInfo> messages;
            Dictionary<string, string?> people;
            Dictionary<string, string?> titles;
            HashSet<string> self;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                messages = corpus.QueryIn(null, "SELECT " + InfoColumns + " WHERE m.message_id IN ({keys})", messageIds, ReadInfo).OrderBy(m => m.AtMs).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
                people = PersonNames(corpus, personIds.Concat(messages.Select(m => m.Person)));
                titles = corpus.QueryIn(null, "SELECT conversation_id, title FROM conversations WHERE conversation_id IN ({keys})",
                    conversationIds.Concat(messages.Select(m => m.Conversation)).Distinct(StringComparer.Ordinal).ToList(), r => (r.GetString(0), r.GetStringOrNull(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                self = ClassificationModule.SelfIdentities(corpus);
            }
            List<(string Id, string Person, string Attribute, string? Value, long At, bool Shown)> facts;
            using (var intel = Context.Stores.Intel.OpenRead())
                facts = intel.QueryIn(null, "SELECT assertion_id, subject_id, attribute, value_text, recorded_at_ms, source_kind, modality FROM assertions WHERE assertion_id IN ({keys}) AND retracted_at_ms IS NULL",
                    assertionIds, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetInt64(4), r.GetStringOrNull(5), r.GetStringOrNull(6)))
                    .Select(f => (f.Item1, f.Item2, f.Item3, f.Item4, f.Item5,
                        MinorGuard.AllowsAttribute(f.Item3, Context.Policy.BandOf(f.Item2, now)) && SensitiveTraits.AllowsAssertion(f.Item3, f.Item6, f.Item7))).ToList();
            var zones = new PersonZones(Context.Stores, Context.Policy);
            string Name(MessageInfo m) => self.Contains(m.AuthorIdentity) ? "Klives" : m.Person != null && people.GetValueOrDefault(m.Person) is { Length: > 0 } n ? n : "someone";

            if (format == "json")
                return JsonSerializer.Serialize(new
                {
                    bundle.BundleId, bundle.Title, bundle.Note, bundle.CreatedBy, bundle.CreatedAtMs, exportedAtMs = now,
                    messages = messages.Select(m => new { m.Id, conversationId = m.Conversation, conversation = titles.GetValueOrDefault(m.Conversation), personId = m.Person, author = Name(m), atMs = m.AtMs,
                        text = m.Text, deleted = m.DeletedAt != null }),
                    people = personIds.Select(p => new { personId = p, name = people.GetValueOrDefault(p) }),
                    conversations = conversationIds.Select(c => new { conversationId = c, title = titles.GetValueOrDefault(c) }),
                    facts = facts.Select(f => f.Shown ? (object)new { assertionId = f.Id, personId = f.Person, f.Attribute, value = f.Value, recordedAtMs = f.At } : new { assertionId = f.Id, withheld = true }),
                    other = bundle.Items.Where(i => i.Kind is not ("message" or "person" or "conversation" or "assertion")),
                }, new JsonSerializerOptions(KernelJson.Options) { WriteIndented = true });

            var sb = new StringBuilder();
            sb.Append("# ").Append(bundle.Title).Append("\n\n");
            sb.Append("Bundle ").Append(bundle.BundleId).Append(", exported ").Append(Instant.ToUtcDateTime(now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("Z.\n\n");
            if (!string.IsNullOrWhiteSpace(bundle.Note)) sb.Append(bundle.Note).Append("\n\n");
            if (personIds.Count > 0)
            {
                sb.Append("## People\n\n");
                foreach (string p in personIds) sb.Append("- ").Append(people.GetValueOrDefault(p) ?? p).Append(" (`").Append(p).Append("`)\n");
                sb.Append('\n');
            }
            if (conversationIds.Count > 0)
            {
                sb.Append("## Conversations\n\n");
                foreach (string c in conversationIds) sb.Append("- ").Append(titles.GetValueOrDefault(c) ?? c).Append(" (`").Append(c).Append("`)\n");
                sb.Append('\n');
            }
            if (messages.Count > 0)
            {
                sb.Append("## Messages\n\n");
                foreach (var g in messages.GroupBy(m => m.Conversation))
                {
                    sb.Append("### ").Append(titles.GetValueOrDefault(g.Key) ?? g.Key).Append("\n\n");
                    foreach (var m in g)
                    {
                        sb.Append("- ").Append(Stamps.Message(m.Id, m.AtMs, zones.At(m.Person, m.AtMs))).Append(" **").Append(Name(m)).Append("**");
                        if (m.DeletedAt != null) sb.Append(" (deleted by its author)");
                        sb.Append(": ").Append((m.Text ?? "").Replace("\r", "").Replace("\n", "\n  ")).Append('\n');
                    }
                    sb.Append('\n');
                }
            }
            if (facts.Count > 0)
            {
                sb.Append("## Facts\n\n");
                foreach (var f in facts)
                    sb.Append("- ").Append(f.Shown ? $"{people.GetValueOrDefault(f.Person) ?? f.Person}: {f.Attribute} = {f.Value} (`{f.Id}`)" : $"`{f.Id}`: withheld (protected attribute)").Append('\n');
                sb.Append('\n');
            }
            var other = bundle.Items.Where(i => i.Kind is not ("message" or "person" or "conversation" or "assertion")).ToList();
            if (other.Count > 0)
            {
                sb.Append("## Other items\n\n");
                foreach (var i in other) sb.Append("- ").Append(i.Kind).Append(" `").Append(i.Id).Append('`').Append(i.Label == null ? "" : " " + i.Label).Append('\n');
            }
            return sb.ToString().TrimEnd() + "\n";
        }
    }
}
