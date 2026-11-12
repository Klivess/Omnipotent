using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Api;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Plans
{
    public static class PlanStatuses
    {
        public const string Upcoming = "upcoming";
        public const string Due = "due";
        public const string Past = "past";
        public const string Kept = "kept";
        public const string Overdue = "overdue";
        public const string Cancelled = "cancelled";
        public const string Recurring = "recurring";
        public const string Undated = "undated";
    }

    public sealed record PlanRecord(
        string PlanId, string Kind, string? Subtype, string OwnerPersonId, string? CounterpartyPersonId, bool InvolvesKlives, bool KlivesPromised, string Title,
        TimeInterval? Scheduled, string? LocalZone, bool AllDay, string Firmness, bool Cancelled, bool Done, bool Rescheduled, string? Recurrence, int? Month, int? Day,
        int? Year, string AssertionId, string NaturalKey, IReadOnlyList<EvidenceRef> Evidence, string? ConversationId, string? GuildId, string? Platform, bool MinorRestricted);

    /// <summary>
    /// <c>knowledge.plans</c> — Plans, Promises &amp; Dates (N8): commitments ("I'll send it tomorrow"), shared plans ("let's play
    /// Saturday") and important dates (birthdays, exams, trips, deadlines), projected from the knowledge store. Times were
    /// resolved in the speaker's local zone at extraction; status is recomputed against the as-of instant and follow-up
    /// evidence (cancelled, moved, done), never stored as a stale label; promises past their time without "done" evidence are
    /// overdue; birthdays recur yearly (Feb 29 falls on Feb 28 in other years, configurable); reminders fire in Klives's zone
    /// (DST-safe, once late after downtime, never a catch-up storm). Minors get no reminders, and their birthdays are never
    /// stored in the first place (MinorGuard). Klives's own promises are highlighted.
    /// </summary>
    public sealed class PlansModule : IntelModuleBase, Kernel.Explore.IExploreSourceProvider
    {
        public const string Id = KnowledgeSchema.PlansModule;
        public const string ProjectKind = "project";
        public const string RemindKind = "remind";
        public const string AgendaModel = "knowledge.agenda";
        public const string PersonPlansModel = "knowledge.plans";
        public const string Feb29Setting = Id + ".feb29_rule";
        public const string AgendaDaysSetting = Id + ".agenda_days";

        private KnowledgeKit kit = null!;

        /// <summary>The datasets this module declares, offered to the Explore mirror (integration, Phase 3).</summary>
        public IReadOnlyList<Kernel.Explore.ExploreSource> ExploreSources => Store.KnowledgeSchema.ExploreSourcesOf(Manifest.Datasets);

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = Id,
            Version = 1,
            Title = "Plans, promises and dates",
            Category = "knowledge",
            Description = "Plans, promises and important dates with status at as-of, recurring birthdays and reminders in Klives's zone.",
            DefaultLane = Lane.Cpu,
            DependsOn = new[] { KnowledgeSchema.DeductionModule },
            MinorRestricted = true,
            Schedules = new[] { new ScheduleTrigger("reminders", Every: TimeSpan.FromMinutes(5)), new ScheduleTrigger("agenda", DailyAtLocal: new TimeOnly(0, 5)) },
            Datasets = new[] { KnowledgeSchema.PlansDataset },
            Settings = new[]
            {
                new SettingDescriptor(Feb29Setting, SettingTypes.String, "\"feb28\"", "Where a 29 February birthday falls in other years: feb28 or mar1."),
                new SettingDescriptor(AgendaDaysSetting, SettingTypes.Int, "60", "How many days ahead the agenda shows.", 1, 400),
            },
            ReadModels = new[]
            {
                new ReadModelDescriptor(AgendaModel, "Agenda", "omniscience.persons.read", 1, 300_000, "Upcoming plans and dates in Klives's zone, overdue promises and Klives's own promises."),
                new ReadModelDescriptor(PersonPlansModel, "Plans of a person", "omniscience.persons.read", 1, 600_000, "A person's plans, promises and important dates with status."),
            },
        };

        public override IReadOnlyList<Storage.Sqlite.Migration> IntelMigrations => KnowledgeSchema.Plans;

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            kit = KnowledgeKit.For(context);
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            foreach (var (name, _) in context.DueSchedules)
            {
                if (name == "reminders") yield return Job(RemindKind, priority: 120);
                if (name == "agenda") yield return Job(ProjectKind, "agenda", "klives", 60);
            }
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            switch (job.Job.Kind)
            {
                case ProjectKind when job.Job.SubjectKind == "person":
                {
                    string person = kit.Corpus.Canonical(job.Job.SubjectId!);
                    int n = await ProjectAsync(person, job.Cancellation).ConfigureAwait(false);
                    await WriteAgendaAsync(job.Cancellation).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { person, plans = n }, KernelJson.Options);
                }
                case ProjectKind:
                    await WriteAgendaAsync(job.Cancellation).ConfigureAwait(false);
                    return null;
                case RemindKind:
                    return JsonSerializer.Serialize(await FireRemindersAsync(job.Cancellation).ConfigureAwait(false), KernelJson.Options);
                default:
                    throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
            }
        }

        // ── projection ───────────────────────────────────────────────────────────

        private static readonly HashSet<string> PlanAttributes = new(StringComparer.Ordinal) { "plan", "promise", "important_date", "birthday" };

        public IReadOnlyList<PlanRecord> Build(string person, long asOfMs)
        {
            var band = Context.Policy.BandOf(person, asOfMs);
            var facts = kit.Assertions.Query(new AssertionQuery
            {
                SubjectIds = kit.Corpus.PersonIdsOf(person), AtMs = asOfMs, Mode = KnowledgeMode.AsKnownAt, Producer = Producers.Extract,
                Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed, AssertionStatus.Contested, AssertionStatus.Proposed }, IncludeInferred = false, Limit = 5_000,
            });
            var self = kit.Corpus.SelfPersonIds().ToHashSet(StringComparer.Ordinal);
            var result = new List<PlanRecord>();
            foreach (var f in facts.Where(f => PlanAttributes.Contains(f.Record.Attribute)))
            {
                var r = f.Record;
                if (r.Status == AssertionStatus.Proposed && f.EffectiveProbability < 0.35) continue;
                if (r.Value.Polarity == Polarity.Negated) continue;
                var detail = r.Value.Detail;
                string kind = r.Attribute switch { "promise" => "promise", "plan" => "plan", "birthday" => "birthday", _ => "date" };
                string? subtype = Str(detail, "plan", "subtype");
                string firmness = Str(detail, "plan", "firmness") ?? "firm";
                string owner = kit.Corpus.Canonical(Str(detail, "promiser") ?? person);
                string? counterparty = Str(detail, "promisee") is { } pe ? kit.Corpus.Canonical(pe) : null;
                bool involvesKlives = self.Contains(owner) || (counterparty != null && self.Contains(counterparty)) || (kind != "promise" && self.Contains(person));
                bool klivesPromised = kind == "promise" && self.Contains(owner);
                var updates = detail is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("planUpdates", out var u) && u.ValueKind == JsonValueKind.Array
                    ? u.EnumerateArray().ToList() : new List<JsonElement>();
                bool cancelled = updates.Any(x => x.TryGetProperty("change", out var ch) && ch.GetString() == "cancelled");
                bool done = updates.Any(x => x.TryGetProperty("change", out var ch) && ch.GetString() == "done");
                var reschedule = updates.LastOrDefault(x => x.TryGetProperty("change", out var ch) && ch.GetString() == "rescheduled");
                TimeInterval? scheduled = r.ValidFrom;
                bool rescheduled = reschedule.ValueKind == JsonValueKind.Object;
                if (rescheduled && reschedule.TryGetProperty("occurred", out var occ) && occ.ValueKind == JsonValueKind.Object
                    && occ.TryGetProperty("earliestMs", out var e) && occ.TryGetProperty("latestMs", out var l) && e.TryGetInt64(out long em) && l.TryGetInt64(out long lm))
                    scheduled = new TimeInterval(Math.Min(em, lm), Math.Max(em, lm));
                int? month = null, day = null, year = null;
                if (kind == "birthday")
                {
                    month = Int(detail, "age", "month");
                    day = Int(detail, "age", "day");
                    year = Int(detail, "age", "year");
                    if (month == null || day == null) continue; // a birthday without a usable date is not schedulable
                }
                bool allDay = kind == "birthday" || scheduled is not { } s || s.WidthMs > 3 * 3_600_000L;
                var dims = Dims(r.AssertionId);
                var ownerBand = owner == person ? band : Context.Policy.BandOf(owner, asOfMs);
                string title = kind == "birthday" ? $"{kit.Corpus.DisplayName(person) ?? "Someone"}'s birthday" : (r.ValueText ?? r.Value.Text);
                result.Add(new PlanRecord(NaturalKeys.Id("pl", r.NaturalKey, 20), kind, subtype, owner, counterparty, involvesKlives, klivesPromised, title, scheduled,
                    dims.Zone, allDay, firmness, cancelled, done, rescheduled, kind == "birthday" ? "yearly" : null, month, day, year, r.AssertionId, r.NaturalKey,
                    kit.Assertions.Evidence(r.AssertionId), dims.Conversation, dims.Guild, dims.Platform, MinorGuard.IsRestricted(ownerBand) || MinorGuard.IsRestricted(band)));
            }
            return result;
        }

        private (string? Conversation, string? Guild, string? Platform, string? Zone) Dims(string assertionId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            var d = c.Query(null, "SELECT conversation_id, guild_id, platform, author_person_id, occurred_at_ms FROM kn_assertion_dims WHERE assertion_id=$a",
                r => (r.GetStringOrNull(0), r.GetStringOrNull(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetInt64(4)), ("$a", assertionId)).FirstOrDefault();
            string? zone = d.Item4 != null ? kit.Zones.ZoneAt(d.Item4, d.Item5)?.Id : null;
            return (d.Item1, d.Item2, d.Item3, zone);
        }

        private static string? Str(JsonElement? detail, params string[] path)
        {
            if (detail is not { ValueKind: JsonValueKind.Object } e) return null;
            foreach (var p in path)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
            }
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }

        private static int? Int(JsonElement? detail, params string[] path)
        {
            if (detail is not { ValueKind: JsonValueKind.Object } e) return null;
            foreach (var p in path)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
            }
            return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : null;
        }

        /// <summary>The status of a plan at <paramref name="asOfMs"/> (recomputed, never stored stale).</summary>
        public static string StatusAt(PlanRecord p, long asOfMs)
        {
            if (p.Cancelled) return PlanStatuses.Cancelled;
            if (p.Done) return p.Kind == "promise" ? PlanStatuses.Kept : PlanStatuses.Past;
            if (p.Recurrence != null) return PlanStatuses.Recurring;
            if (p.Scheduled is not { } s || s.IsUnknown) return PlanStatuses.Undated;
            if (s.EarliestMs != long.MinValue && asOfMs < s.EarliestMs) return PlanStatuses.Upcoming;
            if (s.LatestMs == long.MaxValue || asOfMs <= s.LatestMs) return PlanStatuses.Due;
            return p.Kind == "promise" ? PlanStatuses.Overdue : PlanStatuses.Past;
        }

        /// <summary>The next occurrence (start of the local day in <paramref name="zone"/>) of a yearly date on or after <paramref name="afterMs"/>.
        /// 29 February falls on 28 February (or 1 March) in non-leap years.</summary>
        public static long NextYearly(int month, int day, long afterMs, TimeZoneInfo zone, string feb29Rule)
        {
            var today = Zones.LocalDate(afterMs, zone);
            for (int y = today.Year; y <= today.Year + 8; y++)
            {
                DateOnly date;
                if (month == 2 && day == 29 && !DateTime.IsLeapYear(y)) date = feb29Rule == "mar1" ? new DateOnly(y, 3, 1) : new DateOnly(y, 2, 28);
                else if (day > DateTime.DaysInMonth(y, month)) continue;
                else date = new DateOnly(y, month, day);
                if (date >= today) return Zones.DayStartUtcMs(date, zone);
            }
            throw new InvalidOperationException("No yearly occurrence found.");
        }

        public long? NextOccurrence(PlanRecord p, long asOfMs)
        {
            if (p.Recurrence == "yearly" && p.Month is int m && p.Day is int d)
                return NextYearly(m, d, asOfMs, Context.Settings.KlivesZone, Context.Settings.Get(Feb29Setting, "feb28"));
            if (p.Scheduled is { } s && !s.IsUnknown) return s.EarliestMs == long.MinValue ? s.LatestMs : s.EarliestMs;
            return null;
        }

        public async Task<int> ProjectAsync(string person, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            var plans = Build(person, now);
            long seq = kit.Corpus.MaxSeq();
            var zone = Context.Settings.KlivesZone;
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                var live = plans.Select(p => p.PlanId).ToHashSet(StringComparer.Ordinal);
                foreach (var p in plans)
                {
                    string status = StatusAt(p, now);
                    c.Exec(tx, """
                        INSERT INTO kn_plans(plan_id, kind, subtype, owner_person_id, counterparty_person_id, involves_klives, klives_promised, title, scheduled_earliest_ms,
                            scheduled_latest_ms, local_zone, all_day, firmness, status, recurrence, month, day, year, assertion_id, natural_key, evidence_json, conversation_id,
                            guild_id, platform, minor_restricted, created_at_ms, updated_at_ms, source_seq)
                        VALUES($id,$k,$st,$o,$cp,$ik,$kp,$t,$se,$sl,$z,$ad,$f,$status,$rec,$mo,$d,$y,$a,$key,$ev,$conv,$g,$pl,$minor,$n,$n,$seq)
                        ON CONFLICT(plan_id) DO UPDATE SET kind=excluded.kind, subtype=excluded.subtype, owner_person_id=excluded.owner_person_id,
                            counterparty_person_id=excluded.counterparty_person_id, involves_klives=excluded.involves_klives, klives_promised=excluded.klives_promised,
                            title=excluded.title, scheduled_earliest_ms=excluded.scheduled_earliest_ms, scheduled_latest_ms=excluded.scheduled_latest_ms,
                            local_zone=excluded.local_zone, all_day=excluded.all_day, firmness=excluded.firmness, status=excluded.status, recurrence=excluded.recurrence,
                            month=excluded.month, day=excluded.day, year=excluded.year, assertion_id=excluded.assertion_id, evidence_json=excluded.evidence_json,
                            conversation_id=excluded.conversation_id, guild_id=excluded.guild_id, platform=excluded.platform, minor_restricted=excluded.minor_restricted,
                            updated_at_ms=excluded.updated_at_ms, source_seq=excluded.source_seq
                        """, ("$id", p.PlanId), ("$k", p.Kind), ("$st", p.Subtype), ("$o", p.OwnerPersonId), ("$cp", p.CounterpartyPersonId), ("$ik", p.InvolvesKlives ? 1 : 0),
                        ("$kp", p.KlivesPromised ? 1 : 0), ("$t", p.Title), ("$se", Bound(p.Scheduled?.EarliestMs)), ("$sl", Bound(p.Scheduled?.LatestMs)), ("$z", p.LocalZone),
                        ("$ad", p.AllDay ? 1 : 0), ("$f", p.Firmness), ("$status", status), ("$rec", p.Recurrence), ("$mo", p.Month), ("$d", p.Day), ("$y", p.Year),
                        ("$a", p.AssertionId), ("$key", p.NaturalKey), ("$ev", JsonSerializer.Serialize(p.Evidence.Take(20), KernelJson.Options)), ("$conv", p.ConversationId),
                        ("$g", p.GuildId), ("$pl", p.Platform), ("$minor", p.MinorRestricted ? 1 : 0), ("$n", now), ("$seq", seq));
                    SyncReminders(c, tx, p, status, zone, now);
                }
                foreach (var stale in c.Query(tx, "SELECT plan_id FROM kn_plans WHERE owner_person_id=$p OR (counterparty_person_id IS NULL AND owner_person_id=$p)", r => r.GetString(0), ("$p", person))
                             .Where(id => !live.Contains(id)).ToList())
                {
                    c.Exec(tx, "UPDATE kn_plans SET status='cancelled', updated_at_ms=$n WHERE plan_id=$i AND status <> 'cancelled'", ("$n", now), ("$i", stale));
                    c.Exec(tx, "UPDATE kn_reminders SET state='cancelled' WHERE plan_id=$i AND state='pending'", ("$i", stale));
                }
            }, ct).ConfigureAwait(false);
            await Context.ReadModels.PutAsync(PersonPlansModel, person, 1, now, seq, plans.Select(p => ToDto(p, now)).ToList(), ct).ConfigureAwait(false);
            return plans.Count;
        }

        private static long? Bound(long? v) => v is long.MinValue or long.MaxValue ? null : v;

        /// <summary>Reminder times in Klives's zone (T7): the day before and the day itself at 09:00 local for dates and birthdays,
        /// two hours before a timed plan, and the due day for Klives's own promises. None for minors, none for cancelled or kept items.</summary>
        private void SyncReminders(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, PlanRecord p, string status, TimeZoneInfo zone, long now)
        {
            bool eligible = !p.MinorRestricted && status is not (PlanStatuses.Cancelled or PlanStatuses.Kept or PlanStatuses.Past)
                            && (p.InvolvesKlives || p.Kind == "birthday" && kit.Corpus.TierOf(p.OwnerPersonId) is "tracked" or "watch");
            if (!eligible)
            {
                c.Exec(tx, "UPDATE kn_reminders SET state='cancelled' WHERE plan_id=$i AND state='pending'", ("$i", p.PlanId));
                return;
            }
            long? occurrence = NextOccurrence(p, now);
            if (occurrence is not long at) return;
            var times = new List<(string Kind, long FireAt)>();
            var localDay = Zones.LocalDate(at, zone);
            long nineOnDay = Zones.ResolveLocal(localDay.ToDateTime(new TimeOnly(9, 0)), zone).UtcMs;
            long nineDayBefore = Zones.ResolveLocal(localDay.AddDays(-1).ToDateTime(new TimeOnly(9, 0)), zone).UtcMs;
            if (p.Kind is "birthday" or "date") { times.Add(("day_before", nineDayBefore)); times.Add(("same_day", nineOnDay)); }
            else if (p.KlivesPromised) times.Add(("due", p.AllDay ? nineOnDay : at - 3_600_000));
            else { times.Add(("day_before", nineDayBefore)); if (!p.AllDay) times.Add(("before", at - 2 * 3_600_000)); else times.Add(("same_day", nineOnDay)); }
            // A reschedule moves the occurrence: reminders of other occurrences are cancelled.
            c.Exec(tx, "UPDATE kn_reminders SET state='cancelled' WHERE plan_id=$i AND state='pending' AND occurrence_ms <> $o", ("$i", p.PlanId), ("$o", at));
            foreach (var (kind, fireAt) in times)
            {
                string id = NaturalKeys.Id("rm", p.PlanId + "|" + kind + "|" + at.ToString(CultureInfo.InvariantCulture), 20);
                c.Exec(tx, "INSERT OR IGNORE INTO kn_reminders(reminder_id, plan_id, occurrence_ms, fire_at_ms, kind, state, created_at_ms) VALUES($id,$p,$o,$f,$k,'pending',$n)",
                    ("$id", id), ("$p", p.PlanId), ("$o", at), ("$f", fireAt), ("$k", kind), ("$n", now));
            }
        }

        /// <summary>Fires due reminders as live events (<c>person.plan.reminder</c>) and as notices (X48: kind <c>plan_reminder</c>,
        /// delivered under Klives's rules). A reminder missed while the service was down fires once, late, with its lateness —
        /// unless the occurrence itself is already past. One whose plan has gone (forgotten, superseded away) or has been held
        /// back by the minor guard since it was set is cancelled, not fired.</summary>
        public async Task<object> FireRemindersAsync(CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            List<(string Id, string Plan, long Occurrence, long FireAt, string Kind)> due;
            using (var c = Context.Stores.Intel.OpenRead())
                due = c.Query(null, "SELECT reminder_id, plan_id, occurrence_ms, fire_at_ms, kind FROM kn_reminders WHERE state='pending' AND fire_at_ms <= $n ORDER BY fire_at_ms LIMIT 200",
                    r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4)), ("$n", now));
            int fired = 0, missed = 0, withheld = 0;
            foreach (var r in due)
            {
                (bool Found, string Title, string Owner, string? Counterparty, bool Restricted) plan;
                using (var c = Context.Stores.Intel.OpenRead())
                    plan = c.Query(null, "SELECT title, owner_person_id, counterparty_person_id, minor_restricted FROM kn_plans WHERE plan_id=$p",
                        x => (true, x.GetString(0), x.GetString(1), x.GetStringOrNull(2), x.GetInt32(3) == 1), ("$p", r.Plan)).FirstOrDefault();
                bool occurrencePast = now > r.Occurrence + 86_400_000L;
                string state = !plan.Found || plan.Restricted ? "cancelled" : occurrencePast ? "missed" : "fired";
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE kn_reminders SET state=$s, fired_at_ms=$n WHERE reminder_id=$i AND state='pending'",
                    ("$s", state), ("$n", state == "cancelled" ? (long?)null : now), ("$i", r.Id)), ct).ConfigureAwait(false);
                if (state == "cancelled") { withheld++; continue; }
                if (occurrencePast) { missed++; continue; }
                string title = plan.Title;
                await Context.Events.PublishAsync("person.plan.reminder", new { reminderId = r.Id, planId = r.Plan, title, r.Kind, occurrenceMs = r.Occurrence, latenessMs = now - r.FireAt }, ct)
                    .ConfigureAwait(false);
                // X48: the reminder is also a notice, delivered under Klives's rules (quiet hours, the hourly cap, the digest).
                if (kit.Notices is { } notices)
                    await notices.NoticeAsync(new Notice("plan|" + r.Id, "plan_reminder", Id, now,
                        new { reminderId = r.Id, planId = r.Plan, title, kind = r.Kind, occurrenceMs = r.Occurrence, latenessMs = now - r.FireAt },
                        NoticeSeverity.Attention, plan.Owner, plan.Counterparty), ct).ConfigureAwait(false);
                fired++;
            }
            return new { fired, missed, withheld };
        }

        // ── agenda ───────────────────────────────────────────────────────────────

        public async Task WriteAgendaAsync(CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            int days = Context.Settings.Get(AgendaDaysSetting, 60);
            var zone = Context.Settings.KlivesZone;
            var persons = new List<string>();
            using (var c = Context.Stores.Intel.OpenRead())
                persons = c.Query(null, "SELECT DISTINCT owner_person_id FROM kn_plans WHERE status <> 'cancelled' AND (involves_klives=1 OR kind='birthday')", r => r.GetString(0));
            var all = persons.SelectMany(p => Build(p, now)).GroupBy(p => p.PlanId).Select(g => g.First()).ToList();
            long horizon = now + days * 86_400_000L;
            var dtos = all.Where(p => !p.MinorRestricted || p.InvolvesKlives).Select(p => ToDto(p, now)).ToList();
            var upcoming = dtos.Where(d => d.Status is PlanStatuses.Upcoming or PlanStatuses.Due or PlanStatuses.Recurring && d.NextOccurrenceMs is long t && t <= horizon && (d.InvolvesKlives || d.Kind == "birthday"))
                .OrderBy(d => d.NextOccurrenceMs).ThenBy(d => d.PlanId, StringComparer.Ordinal).ToList();
            var overdue = dtos.Where(d => d.Status == PlanStatuses.Overdue && d.InvolvesKlives).OrderBy(d => d.ScheduledLatestMs).ToList();
            var mine = dtos.Where(d => d.KlivesPromised && d.Status is not (PlanStatuses.Kept or PlanStatuses.Cancelled)).OrderBy(d => d.NextOccurrenceMs ?? long.MaxValue).ToList();
            await Context.ReadModels.PutAsync(AgendaModel, "klives", 1, now, kit.Corpus.MaxSeq(), new AgendaDto(now, zone.Id, upcoming, overdue, mine), ct).ConfigureAwait(false);
        }

        public PlanItemDto ToDto(PlanRecord p, long now) => new(p.PlanId, p.Kind, p.Subtype, p.OwnerPersonId, p.CounterpartyPersonId, p.InvolvesKlives, p.KlivesPromised,
            p.Title, Bound(p.Scheduled?.EarliestMs), Bound(p.Scheduled?.LatestMs), NextOccurrence(p, now), p.LocalZone, p.AllDay, p.Firmness, StatusAt(p, now), p.Recurrence,
            p.Evidence.Count, p.Evidence.Select(e => e.MessageId).Take(5).ToList(), p.ConversationId);
    }
}
