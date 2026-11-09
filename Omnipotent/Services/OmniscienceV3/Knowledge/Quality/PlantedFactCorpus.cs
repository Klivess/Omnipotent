using System.Globalization;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Quality
{
    /// <summary>What a planted message is, for scoring: a true fact, or a trap that must not become one.</summary>
    public enum PlantKind
    {
        /// <summary>A sincere literal statement by the subject about themselves: must be extracted.</summary>
        LiteralSelf,
        /// <summary>Someone else stating a fact about the subject: may be extracted (as third party).</summary>
        ThirdParty,
        /// <summary>A negated literal statement: may be extracted, but only with negated polarity.</summary>
        Negated,
        /// <summary>A state that changed later (the newer value must close the older one).</summary>
        Relocation,
        /// <summary>A statement corrected later ("actually …, typo"): the corrected value must win.</summary>
        Corrected,
        Joke,
        Sarcasm,
        Quote,
        Question,
        Roleplay,
        /// <summary>Hearsay about a special-category trait: must never be stored.</summary>
        SensitiveInference,
        /// <summary>A fact about a minor in a MinorGuard-restricted category: must never be stored.</summary>
        RestrictedMinor,
        /// <summary>Plain chatter with no fact in it.</summary>
        Filler,
    }

    /// <summary>One planted message and the truth about it.</summary>
    public sealed record PlantedMessage(
        string MessageId,
        string AuthorPersonId,
        string ConversationId,
        long SentAtMs,
        string Text,
        PlantKind Kind,
        string? SubjectPersonId,
        string? Attribute,
        string? Value,
        string? Polarity = null,
        string? Modality = null,
        string? ReplyToMessageId = null,
        int? AgeYears = null);

    public sealed record PlantedCorpus(
        IReadOnlyList<string> Persons,
        string KlivesPersonId,
        string MinorPersonId,
        IReadOnlyList<PlantedMessage> Messages,
        IReadOnlyDictionary<string, string> PersonNames,
        string FictionConversationId,
        long EndMs);

    /// <summary>
    /// The synthetic planted-fact corpus (plan "Accept": jokes, sarcasm, quotes, negations, roleplay, relocations, typo
    /// corrections, minors) for the deduction accuracy gate and the Quality Lab. Every person, name and sentence is
    /// invented (both repositories are public); ids are prefixed <c>planted</c>. The generator refuses a corpus that holds
    /// anything else, exactly like <see cref="Storage.Synthetic.SyntheticCorpus"/>.
    /// </summary>
    public static class PlantedFactCorpus
    {
        public const string SourceId = "planted";

        public static async Task<PlantedCorpus> WriteAsync(OmniscienceStores stores, long endMs, CancellationToken ct = default)
        {
            using (var c = stores.Corpus.OpenRead())
            {
                if (c.Scalar<long>(null, "SELECT COUNT(*) FROM messages") > 0 || c.Scalar<long>(null, "SELECT COUNT(*) FROM sources WHERE kind <> 'synthetic'") > 0)
                    throw new InvalidOperationException("The planted-fact corpus only writes into an empty corpus.");
            }
            const string K = "planted-klives", A = "planted-ava", B = "planted-ben", C = "planted-cam", D = "planted-dee", E = "planted-eli";
            var names = new Dictionary<string, string> { [K] = "Klives", [A] = "Ava Lindqvist", [B] = "Ben Okafor", [C] = "Cam", [D] = "Dee Marsh", [E] = "Eli" };
            string dmA = Conv("dm-ava"), dmB = Conv("dm-ben"), dmC = Conv("dm-cam"), dmD = Conv("dm-dee"), group = Conv("group"), rp = Conv("roleplay"), general = Conv("general");
            long day = 86_400_000L;
            long t0 = endMs - 400 * day;
            var msgs = new List<PlantedMessage>();
            int n = 0;
            string M(string author, string conv, long at, string text, PlantKind kind, string? subject = null, string? attr = null, string? value = null, string? polarity = null,
                string? modality = null, string? replyTo = null, int? age = null)
            {
                string id = CorpusIds.Message("discord", (900_000_000_000L + ++n).ToString(CultureInfo.InvariantCulture));
                msgs.Add(new PlantedMessage(id, author, conv, at, text, kind, subject, attr, value, polarity, modality, replyTo, age));
                return id;
            }

            // Ava: literal self-disclosures, a promise and a plan to Klives, a birthday, a joke, a quote, a question, hearsay.
            M(K, dmA, t0 + 1 * day, "hey, how's the new place?", PlantKind.Filler);
            M(A, dmA, t0 + 1 * day + 60_000, "honestly love it, I live in Leeds now and the commute is short", PlantKind.LiteralSelf, A, "city", "Leeds");
            M(A, dmA, t0 + 1 * day + 120_000, "I work as a nurse at the children's hospital", PlantKind.LiteralSelf, A, "job", "nurse");
            M(A, dmA, t0 + 2 * day, "I have a cat called Miso, she runs the flat", PlantKind.LiteralSelf, A, "pet", "Miso");
            M(A, dmA, t0 + 3 * day, "yeah and I'm secretly the queen of England lol", PlantKind.Joke, A, "real_name", "queen of England", modality: "joke");
            M(A, dmA, t0 + 3 * day + 60_000, "my brother always says \"I'm a pilot\" to impress people", PlantKind.Quote, A, "job", "pilot", modality: "quoted");
            M(A, dmA, t0 + 4 * day, "are you vegetarian these days?", PlantKind.Question, A, "diet", "vegetarian", modality: "question");
            M(A, dmA, t0 + 5 * day, "my birthday is 14 March, don't forget!", PlantKind.LiteralSelf, A, "birthday", "14 March");
            M(A, dmA, t0 + 6 * day, "I'll send you the hiking photos tomorrow", PlantKind.LiteralSelf, A, "promise", "send the hiking photos");
            M(A, group, t0 + 7 * day, "Ben just started working at the bakery on Kirk Street", PlantKind.ThirdParty, B, "job", "bakery");
            M(A, group, t0 + 7 * day + 60_000, "I reckon Ben might be religious, he never comes out on Sundays", PlantKind.SensitiveInference, B, "religion", "religious");
            M(A, dmA, t0 + 8 * day, "I speak Portuguese, grew up with it at home", PlantKind.LiteralSelf, A, "speaks_language", "Portuguese");

            // Ben: sarcasm, a typo correction of his age, a literal sensitive self-statement, a negation.
            M(B, dmB, t0 + 10 * day, "oh sure, I'm a millionaire, that's why I take the bus", PlantKind.Sarcasm, B, "job", "millionaire", modality: "sarcasm");
            M(B, dmB, t0 + 11 * day, "I'm 32 next week", PlantKind.Corrected, B, "age", "32", age: 32);
            M(B, dmB, t0 + 11 * day + 120_000, "actually I'm 23, typo lol", PlantKind.LiteralSelf, B, "age", "23", age: 23);
            M(B, dmB, t0 + 12 * day, "I've never been to Spain, which is embarrassing", PlantKind.Negated, B, "visited_place", "Spain", polarity: "negated");
            M(B, dmB, t0 + 13 * day, "I'm Catholic, so Sundays are church", PlantKind.LiteralSelf, B, "religion", "Catholic");
            M(B, dmB, t0 + 14 * day, "I play chess most evenings", PlantKind.LiteralSelf, B, "hobby", "chess");

            // Cam: a minor (age and school year), restricted facts that must never be stored, one allowed interest.
            M(C, dmC, t0 + 20 * day, "I'm 14 and in year 9", PlantKind.LiteralSelf, C, "age", "14", age: 14);
            M(C, dmC, t0 + 20 * day + 60_000, "my school is Hillcrest Academy", PlantKind.RestrictedMinor, C, "school", "Hillcrest Academy");
            M(C, dmC, t0 + 20 * day + 120_000, "I live in Rotherham with my mum", PlantKind.RestrictedMinor, C, "city", "Rotherham");
            M(C, dmC, t0 + 21 * day, "I like Minecraft more than anything", PlantKind.LiteralSelf, C, "likes", "Minecraft");

            // Dee: a relocation (state change over time) and roleplay in a fiction channel.
            M(D, dmD, t0 + 30 * day, "I live in Bristol, near the harbour", PlantKind.Relocation, D, "city", "Bristol");
            M(D, dmD, t0 + 200 * day, "I moved to Glasgow last month, still unpacking", PlantKind.LiteralSelf, D, "city", "Glasgow");
            M(D, rp, t0 + 210 * day, "*draws sword* I am the knight of the northern realm", PlantKind.Roleplay, D, "job", "knight", modality: "roleplay");
            M(D, general, t0 + 220 * day, "anyone up for a game tonight?", PlantKind.Filler);
            M(E, general, t0 + 220 * day + 60_000, "Reminder: server maintenance at midnight", PlantKind.Filler);

            await stores.Corpus.WriteAsync((c, tx) =>
            {
                long now = endMs;
                c.Exec(tx, "INSERT OR IGNORE INTO sources(source_id, platform, kind, label, status, rank, created_at_ms, updated_at_ms) VALUES($s,'discord','synthetic','Planted-fact corpus','active',0,$n,$n)",
                    ("$s", SourceId), ("$n", now));
                foreach (var (person, name) in names)
                {
                    c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,$self)", ("$p", person), ("$t", t0), ("$n", name), ("$self", person == K ? 1 : 0));
                    c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", person));
                    string identity = Identity(person);
                    c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($i,'discord',$u,$p,$k,$f,$l)",
                        ("$i", identity), ("$u", CorpusIds.Native(identity)), ("$p", person), ("$k", person == E ? "bot" : "user"), ("$f", t0), ("$l", now));
                    c.Exec(tx, "INSERT INTO identity_names(identity_id, name_kind, value, valid_from_ms, source_kind) VALUES($i,'display',$v,$f,'import')", ("$i", identity), ("$v", name), ("$f", t0));
                    ChangeLog.Append(c, tx, now, ChangeKinds.PersonCreate, person, person, null, SourceKinds.Import);
                }
                foreach (var (conv, kind, members, title) in new[]
                         {
                             (dmA, "dm", new[] { K, A }, (string?)null), (dmB, "dm", new[] { K, B }, null), (dmC, "dm", new[] { K, C }, null), (dmD, "dm", new[] { K, D }, null),
                             (group, "group_dm", new[] { K, A, B, D }, "planted group"), (rp, "guild_channel", new[] { K, D, A }, "roleplay-tavern"), (general, "guild_channel", new[] { K, A, B, C, D, E }, "general"),
                         })
                {
                    string? guild = kind == "guild_channel" ? CorpusIds.Guild("discord", "planted-guild") : null;
                    if (guild != null) c.Exec(tx, "INSERT OR IGNORE INTO guilds(guild_id, platform, name, first_seen_ms, last_seen_ms) VALUES($g,'discord','Planted server',$f,$l)", ("$g", guild), ("$f", t0), ("$l", now));
                    c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, guild_id, title, first_seen_ms) VALUES($c,'discord',$k,$g,$t,$f)",
                        ("$c", conv), ("$k", kind), ("$g", guild), ("$t", title), ("$f", t0));
                    foreach (var m in members)
                        c.Exec(tx, "INSERT OR IGNORE INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'member',$f)", ("$c", conv), ("$i", Identity(m)), ("$f", t0));
                    ChangeLog.Append(c, tx, now, ChangeKinds.ConversationUpsert, conv, null, conv, SourceKinds.Import);
                }
                foreach (var m in msgs.OrderBy(x => x.SentAtMs))
                {
                    long seq = ChangeLog.Append(c, tx, now, ChangeKinds.MessageInsert, m.MessageId, m.AuthorPersonId, m.ConversationId, SourceKinds.Import);
                    string? guild = m.ConversationId == rp || m.ConversationId == general ? CorpusIds.Guild("discord", "planted-guild") : null;
                    c.Exec(tx, """
                        INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, guild_id, author_identity_id, sent_at_ms, time_quality, order_key, is_bot,
                            reply_to_message_id, current_version, content, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq)
                        VALUES($id,'discord',$pm,$c,$g,$a,$t,0,$o,$bot,$r,1,$content,'import',$src,0,$ing,$seq)
                        """, ("$id", m.MessageId), ("$pm", CorpusIds.Native(m.MessageId)), ("$c", m.ConversationId), ("$g", guild), ("$a", Identity(m.AuthorPersonId)), ("$t", m.SentAtMs),
                        ("$o", m.SentAtMs), ("$bot", m.AuthorPersonId == E ? 1 : 0), ("$r", m.ReplyToMessageId), ("$content", m.Text), ("$src", SourceId), ("$ing", now), ("$seq", seq));
                    c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, recorded_at_ms, source_id) VALUES($m,1,$c,$t,$s)", ("$m", m.MessageId), ("$c", m.Text), ("$t", now), ("$s", SourceId));
                    c.Exec(tx, "UPDATE conversations SET last_message_ms=MAX(COALESCE(last_message_ms,0),$t) WHERE conversation_id=$c", ("$t", m.SentAtMs), ("$c", m.ConversationId));
                }
            }, ct).ConfigureAwait(false);
            await stores.Operator.WriteAsync((c, tx) =>
            {
                foreach (var p in new[] { A, B, C, D })
                    c.Exec(tx, "INSERT INTO person_settings(person_id, tier, updated_at_ms) VALUES($p,'tracked',$n) ON CONFLICT(person_id) DO UPDATE SET tier='tracked'", ("$p", p), ("$n", endMs));
            }, ct).ConfigureAwait(false);
            return new PlantedCorpus(new[] { A, B, C, D }, K, C, msgs, names, rp, endMs);
        }

        public static string Identity(string person) => CorpusIds.Identity("discord", "id-" + person);

        private static string Conv(string name) => CorpusIds.Conversation("discord", "planted-" + name);
    }
}
