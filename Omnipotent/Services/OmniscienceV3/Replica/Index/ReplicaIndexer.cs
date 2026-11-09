using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    public sealed record IndexOptions
    {
        /// <summary>Consecutive messages by the person closer than this, with nobody else in between, form one burst.</summary>
        public TimeSpan BurstGap { get; init; } = TimeSpan.FromMinutes(3);
        /// <summary>Messages by others this long before a burst are its stimulus; a burst with none is an initiation.</summary>
        public TimeSpan StimulusWindow { get; init; } = TimeSpan.FromMinutes(30);
        public int MaxStimulusMessages { get; init; } = 8;
        public int MaxContextMessages { get; init; } = 4;
        public int MaxReplyMessages { get; init; } = 12;
        /// <summary>MinorGuard: messages the person wrote before this instant (while a minor) are never used.</summary>
        public long? AdultFromMs { get; init; }
        /// <summary>Data cutoff (the version's watermark time).</summary>
        public long UpToMs { get; init; } = long.MaxValue;
        /// <summary>Bound on the person's authored messages read (most recent first).</summary>
        public int MaxMessages { get; init; } = 150_000;
        /// <summary>When false only the counts are produced (readiness), no pairs are kept.</summary>
        public bool KeepPairs { get; init; } = true;
    }

    /// <summary>Data-sufficiency numbers of an index pass (readiness shows them, F015).</summary>
    public sealed record IndexStats
    {
        public int AuthoredMessages { get; init; }
        public int Replies { get; init; }
        public int Initiations { get; init; }
        public int ExplicitReplies { get; init; }
        public int RepliesToSelf { get; init; }
        public int NonAuthored { get; init; }
        public int Deleted { get; init; }
        public int MinorPeriodExcluded { get; init; }
        public int BotCommands { get; init; }
        public int Conversations { get; init; }
        public IReadOnlyDictionary<string, int> ByContext { get; init; } = new Dictionary<string, int>();
        public long? FirstMs { get; init; }
        public long? LastMs { get; init; }
        public bool Truncated { get; init; }
        public bool IsBot { get; init; }
    }

    public sealed record IndexResult(PersonScope Person, IReadOnlyList<ReplicaPair> Pairs, IndexStats Stats, long SourceSeq);

    /// <summary>
    /// Builds the replica index (plan "Index"): every burst the person wrote, with the stimulus it answered (the preceding
    /// messages by others within the window, plus an explicitly replied-to message however old), the context kind, the
    /// addressee (reply target, DM counterpart, mention, or adjacency: F277/F278), timing and latency. Standalone
    /// initiations are kept. Text is the version the person first sent (edits made later never leak into an earlier
    /// as-of), deleted messages are never shown (a deleted stimulus becomes a placeholder), bots/system/webhook/forwarded/
    /// code/bot-command/paste text is not the person's prose (F272), and messages from a minor period are never read
    /// (MinorGuard). Reads are index seeks on the corpus (author/time and conversation/time indexes): one range query per
    /// cluster of the person's activity.
    /// </summary>
    public sealed class ReplicaIndexer
    {
        /// <summary>The person's own lines are written under this token and named at composition time, with the name they
        /// carried as of the cutoff (plan T5; a later rename never appears in an earlier as-of prompt).</summary>
        public const string SelfToken = "{{SELF}}";

        private readonly SqliteConnection corpus;
        private readonly Func<string, long, (TimeZoneInfo? Zone, string Source)> zoneOf;

        public ReplicaIndexer(SqliteConnection corpus, Func<string, long, (TimeZoneInfo? Zone, string Source)> zoneOf)
        {
            this.corpus = corpus;
            this.zoneOf = zoneOf;
        }

        private sealed record Msg(string Id, string ConversationId, string? GuildId, string Platform, string Author, long SentAt, int Quality, long OrderKey,
            bool Bot, bool System, bool Webhook, string? ReplyTo, bool Forwarded, string? Text, bool Edited, bool Deleted, long Seq);

        private sealed record ConvInfo(string Id, string ContextKind, string? GuildId, IReadOnlyList<string> Members);

        /// <summary>Everything one person's pass shares.</summary>
        private sealed class Pass
        {
            public required PersonScope Person { get; init; }
            public required HashSet<string> Mine { get; init; }
            public required HashSet<string> SelfPersons { get; init; }
            public required IdentityDirectory Directory { get; init; }
            public required NameBook Names { get; init; }
            public required Dictionary<string, List<string>> Media { get; init; }
            public required Dictionary<string, List<(string Person, string Identity)>> Mentions { get; init; }
            public required IndexOptions Options { get; init; }
            public required long SourceSeq { get; init; }
        }

        public IndexResult Build(PersonScope person, IndexOptions o)
        {
            var mine = new HashSet<string>(person.IdentityIds, StringComparer.Ordinal);
            var directory = new IdentityDirectory(corpus);
            directory.Load(person.IdentityIds);
            bool isBot = person.IdentityIds.Count > 0 && person.IdentityIds.All(i => directory.Get(i)?.IsBot == true);
            long sourceSeq = Storage.ChangeLog.MaxSeq(corpus);

            // 1. The person's authored messages: the most recent up to the bound, then in chronological order.
            var own = new List<Msg>();
            bool truncated = false;
            foreach (var chunk in person.IdentityIds.Chunk(400))
            {
                var names = chunk.Select((_, i) => "$i" + i).ToArray();
                var args = chunk.Select((id, i) => (names[i], (object?)id)).Append(("$up", o.UpToMs)).Append(("$lim", o.MaxMessages + 1)).ToArray();
                var rows = corpus.Query(null, $"""
                    SELECT {MsgColumns} FROM messages WHERE author_identity_id IN ({string.Join(",", names)}) AND sent_at_ms <= $up
                    ORDER BY sent_at_ms DESC LIMIT $lim
                    """, MapMsg, args);
                if (rows.Count > o.MaxMessages) truncated = true;
                own.AddRange(rows);
            }
            if (own.Count > o.MaxMessages) truncated = true;
            own = own.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.OrderKey).Take(o.MaxMessages)
                .OrderBy(m => m.SentAt).ThenBy(m => m.OrderKey).ToList();

            int minorExcluded = 0;
            if (o.AdultFromMs is long adultFrom)
            {
                minorExcluded = own.Count(m => m.SentAt < adultFrom);
                own = own.Where(m => m.SentAt >= adultFrom).ToList();
            }
            int deleted = own.Count(m => m.Deleted);

            // Version 1 text of edited messages: what the person actually sent at the time.
            var firstText = corpus.QueryIn(null, "SELECT message_id, content FROM message_versions WHERE version=1 AND message_id IN ({keys})",
                own.Where(m => m.Edited).Select(m => m.Id).ToList(), r => (Id: r.GetString(0), Text: r.GetStringOrNull(1)))
                .ToDictionary(x => x.Id, x => x.Text, StringComparer.Ordinal);
            own = own.Select(m => m.Edited && firstText.TryGetValue(m.Id, out var t) ? m with { Text = t } : m).ToList();

            var ids = own.Select(m => m.Id).ToList();
            var pass = new Pass
            {
                Person = person,
                Mine = mine,
                SelfPersons = People.SelfPersons(corpus).ToHashSet(StringComparer.Ordinal),
                Directory = directory,
                Names = new NameBook(corpus),
                Media = LoadMedia(ids),
                Mentions = LoadMentions(ids, directory),
                Options = o,
                SourceSeq = sourceSeq,
            };

            var pairs = new List<ReplicaPair>();
            var byContext = ContextKinds.All.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
            int replies = 0, initiations = 0, explicitReplies = 0, toSelf = 0, nonAuthored = 0, botCommands = 0;
            var conversations = own.Select(m => m.ConversationId).Distinct(StringComparer.Ordinal).ToList();
            var convInfo = LoadConversations(conversations);

            foreach (var convId in conversations)
            {
                var info = convInfo.GetValueOrDefault(convId) ?? new ConvInfo(convId, ContextKinds.Other, null, Array.Empty<string>());
                var myMessages = own.Where(m => m.ConversationId == convId).ToList();
                foreach (var cluster in Clusters(myMessages, o.StimulusWindow))
                {
                    var timeline = Timeline(convId, cluster, mine, o);
                    var position = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < timeline.Count; i++) position[timeline[i].Id] = i;
                    directory.Load(timeline.Select(m => m.Author));
                    pass.Names.Load(timeline.Select(m => m.Author));

                    foreach (var burst in Bursts(timeline, mine, o))
                    {
                        var authored = new List<(Msg M, string Line)>();
                        foreach (var m in burst)
                        {
                            var (line, isProse, isCommand) = AuthoredLine(m, timeline, position, pass.Media);
                            if (isCommand) botCommands++;
                            if (line == null) { if (!m.Deleted && !isCommand) nonAuthored++; continue; }
                            if (!isProse) nonAuthored++;
                            authored.Add((m, line));
                        }
                        if (authored.Count == 0) continue;
                        if (authored.Count > o.MaxReplyMessages) authored = authored.Take(o.MaxReplyMessages).ToList();

                        var pair = MakePair(pass, info, burst, authored, timeline, position);
                        if (pair.IsInitiation) initiations++;
                        else replies++;
                        if (pair.ExplicitReply) explicitReplies++;
                        if (pair.PartnerIsSelf && !pair.IsInitiation) toSelf++;
                        byContext[pair.ContextKind] = byContext.GetValueOrDefault(pair.ContextKind) + 1;
                        if (o.KeepPairs) pairs.Add(pair);
                    }
                }
            }

            var stats = new IndexStats
            {
                AuthoredMessages = own.Count(m => !m.Deleted),
                Replies = replies,
                Initiations = initiations,
                ExplicitReplies = explicitReplies,
                RepliesToSelf = toSelf,
                NonAuthored = nonAuthored,
                Deleted = deleted,
                MinorPeriodExcluded = minorExcluded,
                BotCommands = botCommands,
                Conversations = conversations.Count,
                ByContext = byContext,
                FirstMs = own.Count == 0 ? null : own[0].SentAt,
                LastMs = own.Count == 0 ? null : own[^1].SentAt,
                Truncated = truncated,
                IsBot = isBot,
            };
            return new IndexResult(person, pairs.OrderBy(p => p.ReplyAtMs).ThenBy(p => p.PairId, StringComparer.Ordinal).ToList(), stats, sourceSeq);
        }

        // ── corpus reads ─────────────────────────────────────────────────────────

        private const string MsgColumns = "message_id, conversation_id, guild_id, platform, author_identity_id, sent_at_ms, time_quality, order_key, is_bot, is_system, is_webhook, " +
                                          "reply_to_message_id, forwarded_from_json IS NOT NULL, content, current_version > 1, deleted_at_ms IS NOT NULL, ingest_seq";

        private static Msg MapMsg(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt32(6), r.GetInt64(7),
            r.GetInt32(8) == 1, r.GetInt32(9) == 1, r.GetInt32(10) == 1, r.GetStringOrNull(11), r.GetInt32(12) == 1, r.GetStringOrNull(13),
            r.GetInt32(14) == 1, r.GetInt32(15) == 1, r.GetInt64(16));

        /// <summary>The conversation around one cluster of own messages: one range read (capped for very busy channels),
        /// own rows replaced by their version-1 copies, minor-period own rows removed.</summary>
        private List<Msg> Timeline(string conversationId, List<Msg> cluster, HashSet<string> mine, IndexOptions o)
        {
            long from = cluster[0].SentAt - (long)o.StimulusWindow.TotalMilliseconds;
            long to = cluster[^1].SentAt;
            var rows = corpus.Query(null, $"SELECT {MsgColumns} FROM messages WHERE conversation_id=$c AND sent_at_ms >= $f AND sent_at_ms <= $t ORDER BY sent_at_ms, order_key LIMIT 5000",
                MapMsg, ("$c", conversationId), ("$f", from), ("$t", to));
            var ownById = cluster.ToDictionary(m => m.Id, StringComparer.Ordinal);
            var result = new List<Msg>(rows.Count + cluster.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in rows)
            {
                if (ownById.TryGetValue(m.Id, out var own)) { result.Add(own); seen.Add(m.Id); continue; }
                // Own messages outside this cluster (a minor period, beyond the read bound) are not the person's to use.
                if (mine.Contains(m.Author)) continue;
                result.Add(m);
            }
            foreach (var m in cluster) if (!seen.Contains(m.Id)) result.Add(m);
            return result.OrderBy(m => m.SentAt).ThenBy(m => m.OrderKey).ToList();
        }

        private Msg? ById(string messageId) =>
            corpus.Query(null, $"SELECT {MsgColumns} FROM messages WHERE message_id=$m", MapMsg, ("$m", messageId)).FirstOrDefault();

        private Dictionary<string, ConvInfo> LoadConversations(IReadOnlyCollection<string> ids)
        {
            var result = new Dictionary<string, ConvInfo>(StringComparer.Ordinal);
            var rows = corpus.QueryIn(null, "SELECT conversation_id, kind, guild_id FROM conversations WHERE conversation_id IN ({keys})", ids,
                r => (Id: r.GetString(0), Kind: r.GetStringOrNull(1), Guild: r.GetStringOrNull(2)));
            var members = corpus.QueryIn(null, "SELECT conversation_id, identity_id FROM conversation_members WHERE conversation_id IN ({keys})", ids,
                    r => (Conv: r.GetString(0), Identity: r.GetString(1)))
                .GroupBy(x => x.Conv).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Identity).Distinct(StringComparer.Ordinal).ToList());
            foreach (var row in rows)
            {
                var mem = members.GetValueOrDefault(row.Id) ?? Array.Empty<string>();
                string kind = ContextKinds.FromConversationKind(row.Kind);
                if (row.Guild != null) kind = ContextKinds.Guild;
                else if (kind == ContextKinds.Other && mem.Count == 2) kind = ContextKinds.Dm;
                else if (kind == ContextKinds.Other && mem.Count > 2) kind = ContextKinds.GroupDm;
                result[row.Id] = new ConvInfo(row.Id, kind, row.Guild, mem);
            }
            return result;
        }

        private Dictionary<string, List<string>> LoadMedia(IReadOnlyCollection<string> messageIds)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (id, kind, alt) in corpus.QueryIn(null, "SELECT message_id, media_kind, alt_text FROM attachments WHERE message_id IN ({keys}) ORDER BY message_id, idx", messageIds,
                         r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2))))
            {
                if (!map.TryGetValue(id, out var list)) map[id] = list = new List<string>();
                string what = string.IsNullOrWhiteSpace(alt) ? "" : ": " + TextFeatures.HeadTail(alt.Trim().Replace('\n', ' '), 80);
                list.Add($"[sends {MediaWord(kind)}{what}]");
            }
            foreach (var (id, name) in corpus.QueryIn(null, "SELECT message_id, name FROM message_stickers WHERE message_id IN ({keys})", messageIds,
                         r => (r.GetString(0), r.GetStringOrNull(1))))
            {
                if (!map.TryGetValue(id, out var list)) map[id] = list = new List<string>();
                list.Add($"[sends sticker{(string.IsNullOrWhiteSpace(name) ? "" : ": " + name)}]");
            }
            return map;
        }

        private static string MediaWord(string kind) => kind.ToLowerInvariant() switch
        {
            "image" or "gif" => "image",
            "video" => "video",
            "audio" or "voice" => "voice note",
            _ => "file",
        };

        private Dictionary<string, List<(string Person, string Identity)>> LoadMentions(IReadOnlyCollection<string> messageIds, IdentityDirectory directory)
        {
            var rows = corpus.QueryIn(null, "SELECT message_id, target_id FROM message_mentions WHERE target_kind='user' AND message_id IN ({keys})", messageIds,
                r => (Msg: r.GetString(0), Target: r.GetString(1)));
            directory.Load(rows.Select(r => r.Target));
            var map = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            foreach (var (msg, target) in rows)
            {
                var person = directory.Get(target)?.PersonId;
                if (person == null) continue;
                if (!map.TryGetValue(msg, out var list)) map[msg] = list = new List<(string, string)>();
                list.Add((person, target));
            }
            return map;
        }

        // ── segmentation ─────────────────────────────────────────────────────────

        /// <summary>Own messages grouped where consecutive ones are within the stimulus window (one range read each).</summary>
        private static IEnumerable<List<Msg>> Clusters(List<Msg> mine, TimeSpan window)
        {
            var current = new List<Msg>();
            foreach (var m in mine)
            {
                if (current.Count > 0 && m.SentAt - current[^1].SentAt > (long)window.TotalMilliseconds)
                {
                    yield return current;
                    current = new List<Msg>();
                }
                current.Add(m);
            }
            if (current.Count > 0) yield return current;
        }

        /// <summary>Bursts: maximal runs of the person's messages with nobody else in between and gaps within the burst gap.
        /// Day-precision timestamps carry no time of day, so only adjacency counts for them (plan R4).</summary>
        private static IEnumerable<List<Msg>> Bursts(List<Msg> timeline, HashSet<string> mine, IndexOptions o)
        {
            var current = new List<Msg>();
            foreach (var m in timeline)
            {
                if (!mine.Contains(m.Author))
                {
                    if (current.Count > 0) { yield return current; current = new List<Msg>(); }
                    continue;
                }
                if (current.Count > 0)
                {
                    var last = current[^1];
                    bool dayPrecision = last.Quality == (int)TimeQuality.Day || m.Quality == (int)TimeQuality.Day;
                    if (!dayPrecision && m.SentAt - last.SentAt > (long)o.BurstGap.TotalMilliseconds)
                    {
                        yield return current;
                        current = new List<Msg>();
                    }
                }
                current.Add(m);
            }
            if (current.Count > 0) yield return current;
        }

        /// <summary>
        /// The prompt line of one own message, or null when it carries nothing of the person's (deleted, system, bot,
        /// bot command). Placeholders keep real habits visible ("sends images a lot") without inventing content; code,
        /// forwards and pastes are not the person's prose (IsProse=false) and are kept out of style statistics.
        /// </summary>
        private static (string? Line, bool IsProse, bool IsCommand) AuthoredLine(Msg m, List<Msg> timeline, Dictionary<string, int> position, Dictionary<string, List<string>> media)
        {
            if (m.Deleted || m.System || m.Webhook || m.Bot) return (null, false, false);
            var parts = new List<string>();
            bool prose = true;
            string text = m.Text ?? "";
            if (m.Forwarded) { parts.Add("[forwards a message]"); prose = false; text = ""; }
            if (TextFeatures.HasCodeFence(text))
            {
                text = TextFeatures.WithoutCode(text);
                parts.Add("[sends code]");
                if (text.Length == 0) prose = false;
            }
            if (text.Length > 0 && IsBotCommand(m, text, timeline, position)) return (null, false, true);
            if (text.Length >= 400 && text.Count(c => c == '\n') >= 6) { parts.Add("[pastes a long text]"); prose = false; text = ""; }
            if (!string.IsNullOrWhiteSpace(text)) parts.Insert(0, text.Trim());
            if (media.TryGetValue(m.Id, out var att)) parts.AddRange(att);
            if (parts.Count == 0) return (null, false, false);
            if (TextFeatures.StripMarkup(text).Trim().Length == 0) prose = false;
            return (string.Join(" ", parts).Replace("\r", "").Replace('\n', ' ').Trim(), prose, false);
        }

        /// <summary>A command answered by a bot: the next message is a bot reply to it, or a bot answer within 15 s to a
        /// message that starts with a command-prefix symbol (syntax, not meaning).</summary>
        private static bool IsBotCommand(Msg m, string text, List<Msg> timeline, Dictionary<string, int> position)
        {
            if (!position.TryGetValue(m.Id, out int i) || i + 1 >= timeline.Count) return false;
            var next = timeline[i + 1];
            if (!next.Bot) return false;
            if (next.ReplyTo == m.Id) return true;
            char first = text.TrimStart().FirstOrDefault();
            return next.SentAt - m.SentAt <= 15_000 && first is '!' or '/' or '.' or '?' or '$' or '-' or '>' or ';' or '%' or '+';
        }

        // ── pairs ────────────────────────────────────────────────────────────────

        private ReplicaPair MakePair(Pass pass, ConvInfo info, List<Msg> burst, List<(Msg M, string Line)> authored, List<Msg> timeline, Dictionary<string, int> position)
        {
            var o = pass.Options;
            var first = burst[0];
            var last = burst[^1];
            int start = position[first.Id];
            long windowMs = (long)o.StimulusWindow.TotalMilliseconds;
            bool dayPrecision = burst.Any(m => m.Quality == (int)TimeQuality.Day);

            // Stimulus: others' messages since the person's previous message, inside the window; then a little context.
            var stimulus = new List<Msg>();
            var context = new List<Msg>();
            int k = start - 1;
            for (; k >= 0 && stimulus.Count < o.MaxStimulusMessages; k--)
            {
                var m = timeline[k];
                if (pass.Mine.Contains(m.Author)) break;
                if (!dayPrecision && m.Quality != (int)TimeQuality.Day && first.SentAt - m.SentAt > windowMs) { k = -1; break; }
                stimulus.Insert(0, m);
            }
            for (; k >= 0 && context.Count < o.MaxContextMessages; k--)
            {
                var m = timeline[k];
                if (!dayPrecision && first.SentAt - m.SentAt > windowMs * 4) break;
                context.Insert(0, m);
            }

            // An explicit reply anywhere in the burst names its target, however old.
            bool explicitReply = false;
            Msg? target = null;
            foreach (var m in burst)
            {
                if (m.ReplyTo == null) continue;
                var t = position.TryGetValue(m.ReplyTo, out int ti) ? timeline[ti] : ById(m.ReplyTo);
                if (t != null && !pass.Mine.Contains(t.Author) && t.SentAt <= first.SentAt) { target = t; explicitReply = true; break; }
            }
            if (target != null && !stimulus.Any(s => s.Id == target.Id))
            {
                stimulus.Insert(0, target);
                pass.Directory.Load(new[] { target.Author });
                pass.Names.Load(new[] { target.Author });
            }
            bool initiation = stimulus.Count == 0;

            // Addressee: reply target, DM counterpart, mention, then adjacency (F277: reply/mention/DM edges, not co-posters).
            string? partner = null, partnerIdentity = null, source = "none";
            if (target != null && pass.Directory.Get(target.Author) is { } te) { partner = te.PersonId; partnerIdentity = target.Author; source = "reply"; }
            if (partner == null && info.ContextKind == ContextKinds.Dm)
            {
                string? other = info.Members.FirstOrDefault(i => !pass.Mine.Contains(i)) ?? timeline.Select(x => x.Author).FirstOrDefault(a => !pass.Mine.Contains(a));
                if (other != null)
                {
                    pass.Directory.Load(new[] { other });
                    pass.Names.Load(new[] { other });
                    if (pass.Directory.Get(other) is { } oe) { partner = oe.PersonId; partnerIdentity = other; source = "dm"; }
                }
            }
            if (partner == null)
            {
                var mentioned = burst.SelectMany(m => pass.Mentions.GetValueOrDefault(m.Id) ?? new List<(string, string)>()).FirstOrDefault(x => x.Item1 != pass.Person.CanonicalId);
                if (mentioned.Item1 != null) { partner = mentioned.Item1; partnerIdentity = mentioned.Item2; source = "mention"; pass.Names.Load(new[] { mentioned.Item2 }); }
            }
            if (partner == null && stimulus.Count > 0 && pass.Directory.Get(stimulus[^1].Author) is { } se)
            {
                partner = se.PersonId; partnerIdentity = stimulus[^1].Author; source = "adjacency";
            }
            if (partner == pass.Person.CanonicalId) { partner = null; partnerIdentity = null; source = "none"; }
            bool partnerSelf = partner != null && pass.SelfPersons.Contains(partner);

            long? stimulusAt = stimulus.Count == 0 ? null : stimulus.Max(s => s.SentAt);
            long? latency = !initiation && !dayPrecision && stimulusAt is long sa && first.SentAt >= sa ? first.SentAt - sa : null;

            var flags = PairFlags.None;
            foreach (var (m, line) in authored)
            {
                if (TextFeatures.Urls(m.Text ?? "").Count > 0) flags |= PairFlags.Link;
                if (line.Contains("[sends image", StringComparison.Ordinal) || line.Contains("[sends video", StringComparison.Ordinal)
                    || line.Contains("[sends voice", StringComparison.Ordinal) || line.Contains("[sends file", StringComparison.Ordinal)) flags |= PairFlags.Attachment;
                if (line.Contains("[sends code]", StringComparison.Ordinal)) flags |= PairFlags.Code;
                if (line.Contains("[sends sticker", StringComparison.Ordinal)) flags |= PairFlags.Sticker;
                if (m.Edited) flags |= PairFlags.Edited;
                if (TextFeatures.CustomEmoji(m.Text ?? "").Count > 0) flags |= PairFlags.CustomEmoji;
            }
            if (dayPrecision) flags |= PairFlags.DayPrecision;
            if (authored.All(a => TextFeatures.Urls(a.M.Text ?? "").Count > 0 && TextFeatures.StripMarkup(a.M.Text ?? "").Trim().Length == 0)) flags |= PairFlags.LinkOnly;
            if (authored.All(a => string.IsNullOrWhiteSpace(a.M.Text))) flags |= PairFlags.MediaOnly;

            string replyText = string.Join("\n", authored.Select(a => a.Line));
            string stimulusPlain = string.Join("\n", stimulus.Where(s => !s.Deleted).Select(s => TextFeatures.ForPrompt(s.Text ?? "").Replace('\n', ' ')).Where(t => t.Length > 0));
            var (zone, _) = zoneOf(pass.Person.CanonicalId, first.SentAt);
            string partnerLabel = partnerIdentity != null ? pass.Names.NameAt(partnerIdentity, first.SentAt) ?? "someone" : "someone";
            string rendered = Render(pass, info, partnerLabel, context, stimulus, authored, first, zone, initiation);

            return new ReplicaPair
            {
                PairId = "pr:" + first.Id,
                PersonId = pass.Person.CanonicalId,
                ConversationId = info.Id,
                GuildId = info.GuildId ?? first.GuildId,
                Platform = first.Platform,
                ContextKind = info.ContextKind,
                PartnerPersonId = partner,
                PartnerIsSelf = partnerSelf,
                AddresseeSource = source,
                IsInitiation = initiation,
                ExplicitReply = explicitReply,
                ReplyAtMs = first.SentAt,
                ReplyEndMs = last.SentAt,
                StimulusAtMs = stimulusAt,
                LatencyMs = latency,
                ReplyIds = authored.Select(a => a.M.Id).ToList(),
                StimulusIds = stimulus.Select(s => s.Id).ToList(),
                ContextIds = context.Select(s => s.Id).ToList(),
                ReplyText = replyText,
                StimulusPlain = stimulusPlain,
                Rendered = rendered,
                TextHash = TextFeatures.Hash(replyText + "\u0001" + stimulusPlain),
                StimulusHash = TextFeatures.Hash(stimulusPlain.Length == 0 ? "<initiation:" + info.ContextKind + ">" : stimulusPlain),
                TimeQuality = first.Quality,
                Flags = flags,
                SourceSeq = Math.Max(pass.SourceSeq, burst.Max(m => m.Seq)),
            };
        }

        /// <summary>
        /// The exchange as a genuine chat transcript (F298): a stamped header (the person's local time when their zone is
        /// known, plan T3), context and stimulus lines with the names people had at the time (plan T5), and the person's
        /// own lines under <see cref="SelfToken"/>. Deleted messages are placeholders, links are <c>[link: domain]</c>.
        /// </summary>
        private static string Render(Pass pass, ConvInfo info, string partnerLabel, List<Msg> context, List<Msg> stimulus, List<(Msg M, string Line)> authored,
            Msg first, TimeZoneInfo? zone, bool initiation)
        {
            var sb = new StringBuilder();
            string where = info.ContextKind switch
            {
                ContextKinds.Dm => "DM with " + partnerLabel,
                ContextKinds.GroupDm => "group chat",
                ContextKinds.Guild => "server channel",
                _ => "chat",
            };
            sb.Append("— ").Append(Header(first.SentAt, zone, (TimeQuality)first.Quality)).Append(" · ").Append(where).Append(" —\n");
            long? previous = null;
            void Line(Msg m, string text)
            {
                if (previous is long p && m.SentAt - p > 3_600_000 && m.Quality != (int)TimeQuality.Day) sb.Append('(').Append(Gap(m.SentAt - p)).Append(" later)\n");
                previous = m.SentAt;
                string name = pass.Mine.Contains(m.Author) ? SelfToken : pass.Names.NameAt(m.Author, m.SentAt) ?? "someone";
                sb.Append(name).Append(": ").Append(text).Append('\n');
            }
            foreach (var m in context.Concat(stimulus).DistinctBy(m => m.Id).OrderBy(m => m.SentAt).ThenBy(m => m.OrderKey))
            {
                if (pass.Mine.Contains(m.Author) && (m.Deleted || m.System)) continue;
                Line(m, m.Deleted ? "[deleted message]" : TextFeatures.HeadTail(TextFeatures.ForPrompt(m.Text ?? "").Replace('\n', ' '), 600));
            }
            if (initiation && context.Count == 0) sb.Append('(').Append(SelfToken).Append(" starts the conversation)\n");
            foreach (var (m, line) in authored) Line(m, TextFeatures.HeadTail(TextFeatures.ForPrompt(line), 1200));
            return sb.ToString().TrimEnd();
        }

        internal static string Header(long atMs, TimeZoneInfo? zone, TimeQuality quality)
        {
            var inv = CultureInfo.InvariantCulture;
            if (quality == TimeQuality.Day) return Instant.ToUtcDateTime(atMs).ToString("yyyy-MM-dd ddd", inv) + " (time unknown)";
            string utc = Instant.ToUtcDateTime(atMs).ToString("yyyy-MM-dd HH:mm", inv) + "Z";
            if (zone == null) return Instant.ToUtcDateTime(atMs).ToString("yyyy-MM-dd ddd HH:mm", inv) + " UTC (their zone unknown)";
            var local = Zones.ToLocal(atMs, zone);
            return local.ToString("yyyy-MM-dd ddd HH:mm", inv) + " their time (" + Stamps.FormatOffset(Zones.OffsetAt(atMs, zone)) + ") · " + utc;
        }

        internal static string Gap(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            if (t.TotalDays >= 2) return $"{(int)t.TotalDays} days";
            if (t.TotalHours >= 1.5) return $"{(int)Math.Round(t.TotalHours)} h";
            return $"{(int)Math.Round(t.TotalMinutes)} min";
        }
    }
}
