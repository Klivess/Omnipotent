using System.Globalization;
using System.Text;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Evidence
{
    /// <summary>Why a message is evidence about the subject.</summary>
    public enum EvidenceRelation { Authored, ReplyToSubject, RepliedBySubject, Mention }

    /// <summary>A message in the subject's evidence stream (lightweight: what coverage and planning need).</summary>
    public sealed record EvidenceAnchor(string MessageId, int Version, long SentAtMs, long OrderKey, string ConversationId, long IngestSeq, EvidenceRelation Relation);

    /// <summary>A contiguous stretch of one conversation: the anchors plus the surrounding messages that give them meaning.</summary>
    public sealed record Segment(string ConversationId, ConversationInfo? Conversation, IReadOnlyList<CorpusMessage> Messages, IReadOnlyList<string> AnchorIds)
    {
        public long FromMs => Messages.Count == 0 ? 0 : Messages[0].SentAtMs;
        public long ToMs => Messages.Count == 0 ? 0 : Messages[^1].SentAtMs;
    }

    /// <summary>One context-filling unit of evidence: rendered text, the roster, and the reference map for verification.</summary>
    public sealed record EvidenceSlice(
        int Index,
        string EvidenceText,
        string RosterText,
        IReadOnlyList<SliceMessage> Messages,
        IReadOnlyList<string> AnchorIds,
        long FromMs,
        long ToMs,
        int EstimatedTokens,
        Roster Roster)
    {
        private Dictionary<string, SliceMessage>? byRef;
        private Dictionary<string, SliceMessage>? byId;

        public SliceMessage? ByRef(string reference)
        {
            byRef ??= Messages.ToDictionary(m => m.Ref, StringComparer.OrdinalIgnoreCase);
            return byRef.GetValueOrDefault(NormalizeRef(reference));
        }

        public SliceMessage? ById(string messageId)
        {
            byId ??= Messages.ToDictionary(m => m.MessageId, StringComparer.Ordinal);
            return byId.GetValueOrDefault(messageId);
        }

        /// <summary>"m:12", "M12", "[m:12]", 12 or "12" all name the same reference (F196: string refs were dropped).</summary>
        public static string NormalizeRef(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return "";
            string r = reference.Trim().Trim('[', ']', ' ').ToLowerInvariant();
            if (r.StartsWith("m:", StringComparison.Ordinal)) r = r[2..];
            else if (r.StartsWith('m')) r = r[1..];
            return int.TryParse(r.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? "m:" + n.ToString(CultureInfo.InvariantCulture) : "";
        }
    }

    public sealed record AssemblyOptions
    {
        public int ContextBefore { get; init; } = 8;
        public int ContextAfter { get; init; } = 8;
        /// <summary>Two anchors further apart than this start separate segments even in a DM.</summary>
        public long SegmentGapMs { get; init; } = (long)TimeSpan.FromHours(6).TotalMilliseconds;
        public int MaxMessageChars { get; init; } = 4_000;
        public int MaxSegmentMessages { get; init; } = 600;
        /// <summary>At most this many anchors are planned per job, oldest first (the rest stay pending for the next job).</summary>
        public int MaxAnchorsPerJob { get; init; } = 4_000;
    }

    /// <summary>
    /// The evidence assembler (plan "Knowledge &amp; deduction"): per tracked person, a chronological stream of everything they
    /// wrote, the messages they reply to, the replies to them and every message @-mentioning them, assembled into whole
    /// conversation segments (DMs whole, busy channels as windows around the evidence) and packed into slices that fill the
    /// model's real window (fixes F020/F183/F186). Coverage is per message version on the ingest sequence, so backfilled
    /// history and edits are picked up whenever they land (F178/F182/F205), and messages without text (attachments,
    /// stickers, forwards) are included as typed evidence (F180).
    /// </summary>
    public sealed class EvidenceAssembler
    {
        private readonly KnowledgeKit kit;
        private readonly AssemblyOptions options;

        public EvidenceAssembler(KnowledgeKit kit, AssemblyOptions? options = null)
        {
            this.kit = kit;
            this.options = options ?? new AssemblyOptions();
        }

        public AssemblyOptions Options => options;

        private OmniscienceStores Stores => kit.Context.Stores;

        /// <summary>Every anchor of the subject's evidence stream up to <paramref name="asOfMs"/> (lightweight rows, oldest first).</summary>
        public IReadOnlyList<EvidenceAnchor> Anchors(IReadOnlyCollection<string> identityIds, long asOfMs)
        {
            var anchors = new Dictionary<string, EvidenceAnchor>(StringComparer.Ordinal);
            using var c = Stores.Corpus.OpenRead();
            var authored = new List<EvidenceAnchor>();
            foreach (var identity in identityIds)
                authored.AddRange(c.Query(null, """
                    SELECT message_id, current_version, sent_at_ms, order_key, conversation_id, ingest_seq FROM messages
                    WHERE author_identity_id=$a AND sent_at_ms <= $t
                    """, r => new EvidenceAnchor(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetInt64(5), EvidenceRelation.Authored),
                    ("$a", identity), ("$t", asOfMs)));
            foreach (var a in authored) anchors[a.MessageId] = a;
            var authoredIds = authored.Select(a => a.MessageId).ToList();

            // Replies to the subject.
            foreach (var a in c.QueryIn(null, """
                         SELECT message_id, current_version, sent_at_ms, order_key, conversation_id, ingest_seq FROM messages WHERE reply_to_message_id IN ({keys})
                         """, authoredIds, r => new EvidenceAnchor(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetInt64(5), EvidenceRelation.ReplyToSubject)))
                if (a.SentAtMs <= asOfMs) anchors.TryAdd(a.MessageId, a);

            // Messages the subject replied to.
            var parents = c.QueryIn(null, "SELECT reply_to_message_id FROM messages WHERE message_id IN ({keys}) AND reply_to_message_id IS NOT NULL", authoredIds, r => r.GetString(0))
                .Distinct(StringComparer.Ordinal).ToList();
            foreach (var a in c.QueryIn(null, "SELECT message_id, current_version, sent_at_ms, order_key, conversation_id, ingest_seq FROM messages WHERE message_id IN ({keys})", parents,
                         r => new EvidenceAnchor(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetInt64(5), EvidenceRelation.RepliedBySubject)))
                if (a.SentAtMs <= asOfMs) anchors.TryAdd(a.MessageId, a);

            // @-mentions of the subject.
            var mentionIds = c.QueryIn(null, "SELECT message_id FROM message_mentions WHERE target_kind='user' AND target_id IN ({keys})", identityIds.ToList(), r => r.GetString(0))
                .Distinct(StringComparer.Ordinal).ToList();
            foreach (var a in c.QueryIn(null, "SELECT message_id, current_version, sent_at_ms, order_key, conversation_id, ingest_seq FROM messages WHERE message_id IN ({keys})", mentionIds,
                         r => new EvidenceAnchor(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetInt64(5), EvidenceRelation.Mention)))
                if (a.SentAtMs <= asOfMs) anchors.TryAdd(a.MessageId, a);

            return anchors.Values.OrderBy(a => a.SentAtMs).ThenBy(a => a.OrderKey).ThenBy(a => a.IngestSeq).ThenBy(a => a.MessageId, StringComparer.Ordinal).ToList();
        }

        /// <summary>Anchors not yet covered at their current version by <paramref name="promptVersion"/>.</summary>
        public IReadOnlyList<EvidenceAnchor> Uncovered(string personId, IReadOnlyList<EvidenceAnchor> anchors, string promptVersion)
        {
            Dictionary<string, (int Version, string Prompt)> covered;
            using (var c = Stores.Intel.OpenRead())
                covered = c.Query(null, "SELECT message_id, message_version, prompt_version FROM kn_coverage WHERE person_id=$p",
                        r => (Id: r.GetString(0), V: r.GetInt32(1), P: r.GetString(2)), ("$p", personId))
                    .ToDictionary(x => x.Id, x => (x.V, x.P), StringComparer.Ordinal);
            return anchors.Where(a => !covered.TryGetValue(a.MessageId, out var cv) || cv.Version < a.Version || cv.Prompt != promptVersion).ToList();
        }

        /// <summary>Whole-conversation segments around the anchors: DMs and small groups keep everything between nearby anchors;
        /// busy channels keep a window either side of each anchor. Overlapping windows merge.</summary>
        public IReadOnlyList<Segment> Segments(IReadOnlyList<EvidenceAnchor> anchors)
        {
            var conversations = kit.Corpus.Conversations(anchors.Select(a => a.ConversationId).Distinct(StringComparer.Ordinal).ToList());
            var segments = new List<Segment>();
            foreach (var group in anchors.GroupBy(a => a.ConversationId, StringComparer.Ordinal))
            {
                conversations.TryGetValue(group.Key, out var info);
                bool intimate = info?.Kind is "dm" or "group_dm" or "imported_chat";
                var ordered = group.OrderBy(a => a.SentAtMs).ThenBy(a => a.OrderKey).ToList();
                // Cluster anchors by time gap.
                var clusters = new List<List<EvidenceAnchor>>();
                foreach (var a in ordered)
                {
                    if (clusters.Count == 0 || a.SentAtMs - clusters[^1][^1].SentAtMs > options.SegmentGapMs) clusters.Add(new List<EvidenceAnchor>());
                    clusters[^1].Add(a);
                }
                foreach (var cluster in clusters)
                {
                    var messages = new Dictionary<string, CorpusMessage>(StringComparer.Ordinal);
                    if (intimate)
                    {
                        long from = cluster[0].SentAtMs, to = cluster[^1].SentAtMs;
                        foreach (var m in kit.Corpus.Window(group.Key, from, options.ContextBefore, 0)) messages[m.MessageId] = m;
                        foreach (var m in kit.Corpus.ConversationRange(group.Key, from, to, options.MaxSegmentMessages * 2)) messages[m.MessageId] = m;
                        foreach (var m in kit.Corpus.Window(group.Key, to + 1, 0, options.ContextAfter)) messages[m.MessageId] = m;
                    }
                    else
                    {
                        foreach (var a in cluster)
                            foreach (var m in kit.Corpus.Window(group.Key, a.SentAtMs, options.ContextBefore, options.ContextAfter)) messages[m.MessageId] = m;
                    }
                    // Anchors missing from the windows (rare: identical timestamps beyond the window) are added explicitly.
                    var missing = cluster.Where(a => !messages.ContainsKey(a.MessageId)).Select(a => a.MessageId).ToList();
                    foreach (var m in kit.Corpus.Messages(missing)) messages[m.MessageId] = m;
                    var orderedMessages = CorpusReader.Ordered(messages.Values).ToList();
                    var anchorIds = cluster.Select(a => a.MessageId).ToHashSet(StringComparer.Ordinal);
                    // Split very long stretches so one segment never exceeds a slice on its own.
                    foreach (var chunk in orderedMessages.Chunk(options.MaxSegmentMessages))
                    {
                        var ids = chunk.Where(m => anchorIds.Contains(m.MessageId)).Select(m => m.MessageId).ToList();
                        if (ids.Count > 0) segments.Add(new Segment(group.Key, info, chunk, ids));
                    }
                }
            }
            return segments.OrderBy(s => s.FromMs).ThenBy(s => s.ConversationId, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Packs segments, oldest first, into slices of at most <paramref name="tokenBudget"/> estimated tokens (the caller
        /// passes the model's real window minus output, margin, system prompt and profile). A segment bigger than the budget
        /// is split by messages.
        /// </summary>
        public IReadOnlyList<EvidenceSlice> Pack(string subjectPersonId, IReadOnlyList<Segment> segments, int tokenBudget, TokenEstimator estimator)
        {
            var slices = new List<EvidenceSlice>();
            var pending = new List<Segment>();
            int pendingTokens = 0;
            foreach (var segment in segments)
            {
                foreach (var part in SplitToFit(subjectPersonId, segment, tokenBudget, estimator))
                {
                    int cost = EstimateSegment(subjectPersonId, part, estimator);
                    if (pending.Count > 0 && pendingTokens + cost > tokenBudget)
                    {
                        slices.Add(Render(subjectPersonId, slices.Count, pending, estimator));
                        pending = new List<Segment>();
                        pendingTokens = 0;
                    }
                    pending.Add(part);
                    pendingTokens += cost;
                }
            }
            if (pending.Count > 0) slices.Add(Render(subjectPersonId, slices.Count, pending, estimator));
            return slices;
        }

        private IEnumerable<Segment> SplitToFit(string subject, Segment segment, int tokenBudget, TokenEstimator estimator)
        {
            if (EstimateSegment(subject, segment, estimator) <= tokenBudget || segment.Messages.Count <= 1)
            {
                yield return segment;
                yield break;
            }
            int half = segment.Messages.Count / 2;
            var left = segment.Messages.Take(half).ToList();
            var right = segment.Messages.Skip(half).ToList();
            var anchorSet = segment.AnchorIds.ToHashSet(StringComparer.Ordinal);
            foreach (var piece in new[] { left, right })
            {
                var ids = piece.Where(m => anchorSet.Contains(m.MessageId)).Select(m => m.MessageId).ToList();
                if (ids.Count == 0) continue;
                foreach (var s in SplitToFit(subject, new Segment(segment.ConversationId, segment.Conversation, piece, ids), tokenBudget, estimator)) yield return s;
            }
        }

        private int EstimateSegment(string subject, Segment segment, TokenEstimator estimator) =>
            estimator.Estimate(Render(subject, 0, new[] { segment }, estimator).EvidenceText);

        /// <summary>Renders segments as one slice with references m:1..N, the roster and the verification map.</summary>
        public EvidenceSlice Render(string subjectPersonId, int index, IReadOnlyList<Segment> segments, TokenEstimator estimator)
        {
            var roster = new Roster();
            var allMessages = segments.SelectMany(s => s.Messages).ToList();
            var messageIds = allMessages.Select(m => m.MessageId).Distinct(StringComparer.Ordinal).ToList();
            var mentions = kit.Corpus.MentionsOf(messageIds);
            var attachments = kit.Media.For(messageIds).GroupBy(a => a.MessageId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            long from = allMessages.Count == 0 ? 0 : allMessages.Min(m => m.SentAtMs);
            long to = allMessages.Count == 0 ? 0 : allMessages.Max(m => m.SentAtMs);

            // People: the subject first, then every author and every mentioned identity, with the names they used in this period.
            var identityPerson = new Dictionary<string, string?>(StringComparer.Ordinal);
            string? PersonOf(string identityId)
            {
                if (identityPerson.TryGetValue(identityId, out var p)) return p;
                return identityPerson[identityId] = kit.Corpus.PersonOfIdentity(identityId);
            }
            void AddPerson(string personId, RosterRole role)
            {
                if (roster.Contains(personId) && roster.Entry(personId)!.Role <= role) return;
                var ids = kit.Corpus.PersonIdsOf(personId);
                var identities = kit.Corpus.IdentitiesOf(ids).Select(i => i.IdentityId).ToList();
                var names = kit.Corpus.NamesOf(identities, from, to).Select(n => n.Value).ToList();
                if (names.Count == 0) names.AddRange(kit.Corpus.NamesOf(identities).OrderByDescending(n => n.ValidFromMs).Select(n => n.Value).Take(3));
                if (names.Count == 0 && kit.Corpus.DisplayName(personId) is { } dn) names.Add(dn);
                string tier = kit.Corpus.TierOf(personId);
                roster.Add(personId, role, names, tier == "tracked", kit.Corpus.IsSelf(personId));
            }
            AddPerson(subjectPersonId, RosterRole.Subject);
            foreach (var m in allMessages)
                if (m.AuthorPersonId != null) AddPerson(m.AuthorPersonId, m.AuthorPersonId == subjectPersonId ? RosterRole.Subject : RosterRole.Participant);
            foreach (var targets in mentions.Values)
                foreach (var identity in targets)
                    if (PersonOf(identity) is { } p) AddPerson(p, p == subjectPersonId ? RosterRole.Subject : RosterRole.Mentioned);

            string? HandleForPlatformUser(string platform, string platformUserId)
            {
                string identity = CorpusIds.Identity(platform, platformUserId);
                return PersonOf(identity) is { } p && roster.Contains(p) ? roster.HandleFor(p) : null;
            }

            // Parents quoted inline when they fall outside the excerpt (F194).
            var inSlice = messageIds.ToHashSet(StringComparer.Ordinal);
            var outsideParents = kit.Corpus.Messages(allMessages.Where(m => m.ReplyToMessageId != null && !inSlice.Contains(m.ReplyToMessageId)).Select(m => m.ReplyToMessageId!).Distinct(StringComparer.Ordinal).ToList())
                .ToDictionary(m => m.MessageId, StringComparer.Ordinal);
            foreach (var p in outsideParents.Values)
                if (p.AuthorPersonId != null) AddPerson(p.AuthorPersonId, p.AuthorPersonId == subjectPersonId ? RosterRole.Subject : RosterRole.Mentioned);

            var sb = new StringBuilder();
            var sliceMessages = new List<SliceMessage>();
            var refOf = new Dictionary<string, string>(StringComparer.Ordinal);
            int n = 0;
            foreach (var segment in segments)
            {
                sb.Append(SegmentHeader(segment, roster)).Append('\n');
                foreach (var m in segment.Messages)
                {
                    if (refOf.ContainsKey(m.MessageId)) continue; // a message shared by two segments is shown once
                    string reference = "m:" + (++n).ToString(CultureInfo.InvariantCulture);
                    refOf[m.MessageId] = reference;
                    string handle = m.AuthorPersonId != null ? roster.HandleFor(m.AuthorPersonId) : "unknown";
                    var zone = m.AuthorPersonId != null ? kit.Zones.ZoneAt(m.AuthorPersonId, m.SentAtMs) : null;
                    string content = EvidenceRenderer.Content(m.Content, uid => HandleForPlatformUser(m.Platform, uid), options.MaxMessageChars);
                    bool nonAuthored = m.ForwardedJson != null || m.IsBot || m.IsWebhook || m.IsSystem;
                    var line = new StringBuilder();
                    line.Append(EvidenceRenderer.Stamp(reference, m.SentAtMs, m.Quality, zone)).Append(' ').Append(handle);
                    var flags = new List<string>();
                    if (m.IsBot) flags.Add("bot");
                    if (m.IsWebhook) flags.Add("webhook or proxy, not a person's own account");
                    if (m.IsSystem) flags.Add("system message");
                    if (m.EditedAtMs != null) flags.Add("edited");
                    if (m.DeletedAtMs != null) flags.Add("later deleted by its author");
                    if (flags.Count > 0) line.Append(" (").Append(string.Join("; ", flags)).Append(')');
                    line.Append(": ");
                    if (m.ForwardedJson != null) line.Append("[forwarded message, not written by ").Append(handle).Append("] ");
                    line.Append(EvidenceRenderer.Indent(content.Length == 0 ? "(no text)" : content));
                    if (m.ReplyToMessageId != null)
                    {
                        if (refOf.TryGetValue(m.ReplyToMessageId, out var parentRef)) line.Append("\n    ↪ in reply to ").Append(parentRef);
                        else if (outsideParents.TryGetValue(m.ReplyToMessageId, out var parent))
                        {
                            string ph = parent.AuthorPersonId != null && roster.Contains(parent.AuthorPersonId) ? roster.HandleFor(parent.AuthorPersonId) : "someone";
                            string excerpt = EvidenceRenderer.Content(parent.Content, uid => HandleForPlatformUser(parent.Platform, uid), 280).Replace('\n', ' ');
                            line.Append("\n    ↪ in reply to an earlier message (outside this excerpt, not citable) by ").Append(ph)
                                .Append(" at ").Append(Instant.Iso(parent.SentAtMs)).Append(": \"").Append(excerpt).Append('"');
                        }
                        else line.Append("\n    ↪ in reply to a message that is not in the corpus");
                    }
                    if (attachments.TryGetValue(m.MessageId, out var files))
                    {
                        foreach (var f in files)
                        {
                            string label = f.TextKind switch
                            {
                                "ocr" => $"[text inside the {f.MediaKind} (OCR; not typed by the author): \"{EvidenceRenderer.Content(f.Text, _ => null, 1500).Replace('\n', ' ')}\"]",
                                "transcript" => $"[transcript of the {f.MediaKind} (spoken by {handle}): \"{EvidenceRenderer.Content(f.Text, _ => null, 3000).Replace('\n', ' ')}\"]",
                                "caption" => $"[AI-generated caption of the {f.MediaKind}: \"{EvidenceRenderer.Content(f.Text, _ => null, 600).Replace('\n', ' ')}\"]",
                                _ => $"[attachment: {f.MediaKind}{(string.IsNullOrWhiteSpace(f.Filename) ? "" : " \"" + EvidenceRenderer.Sanitize(f.Filename) + "\"")}"
                                     + (string.IsNullOrWhiteSpace(f.Text) ? "]" : $", alt text: \"{EvidenceRenderer.Sanitize(f.Text)}\"]"),
                            };
                            line.Append("\n    ").Append(label);
                        }
                    }
                    sb.Append(line).Append('\n');
                    var conv = segment.Conversation;
                    string? fileText = files == null ? null : string.Join("\n", files.Where(f => !string.IsNullOrWhiteSpace(f.Text)).Select(f => f.Text));
                    sliceMessages.Add(new SliceMessage(reference, m.MessageId, m.Version, m.AuthorPersonId, handle, m.SentAtMs, m.Quality, m.ConversationId, m.GuildId,
                        m.Platform, conv?.Kind, m.FirstSourceKind, m.BestSourceId, m.Content ?? "", m.DeletedAtMs != null, nonAuthored, zone?.Id, m.IngestSeq,
                        string.IsNullOrWhiteSpace(fileText) ? null : fileText));
                }
                sb.Append('\n');
            }
            string evidence = EvidenceRenderer.FenceOpen + "\n" + sb.ToString().TrimEnd() + "\n" + EvidenceRenderer.FenceClose;
            string rosterText = roster.Render();
            var anchors = segments.SelectMany(s => s.AnchorIds).Distinct(StringComparer.Ordinal).ToList();
            return new EvidenceSlice(index, evidence, rosterText, sliceMessages, anchors, from, to, estimator.Estimate(evidence) + estimator.Estimate(rosterText), roster);
        }

        private string SegmentHeader(Segment segment, Roster roster)
        {
            var info = segment.Conversation;
            string kind = info?.Kind switch
            {
                "dm" => "direct messages",
                "group_dm" => "group chat",
                "guild_channel" => "server channel",
                "thread" => "thread",
                "imported_chat" => "imported chat",
                null => "conversation",
                var k => k,
            };
            var parts = new List<string> { kind, info?.Platform ?? "" };
            if (!string.IsNullOrWhiteSpace(info?.GuildName)) parts.Add("server \"" + EvidenceRenderer.Sanitize(info.GuildName) + "\"");
            if (!string.IsNullOrWhiteSpace(info?.Title)) parts.Add("\"" + EvidenceRenderer.Sanitize(info.Title) + "\"");
            double fiction = kit.Fiction.FictionProbability(segment.ConversationId);
            if (fiction >= 0.7) parts.Add("classified as a fiction/roleplay channel: statements here are in character unless clearly out of character");
            return "── " + string.Join(" · ", parts.Where(p => p.Length > 0)) + " · " + Instant.Iso(segment.FromMs)[..10] + " to " + Instant.Iso(segment.ToMs)[..10] + " ──";
        }
    }
}
