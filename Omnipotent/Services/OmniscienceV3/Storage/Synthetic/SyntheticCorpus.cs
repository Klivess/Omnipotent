using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage.Synthetic
{
    /// <summary>Shape of a generated corpus. Defaults give a small, varied corpus; the perf harness asks for millions.</summary>
    public sealed record SyntheticCorpusOptions
    {
        public int Seed { get; init; } = 42;
        public int People { get; init; } = 200;
        public int Guilds { get; init; } = 5;
        public int ChannelsPerGuild { get; init; } = 6;
        public int DirectChats { get; init; } = 40;
        public int WhatsAppGroups { get; init; } = 6;
        public long Messages { get; init; } = 50_000;
        /// <summary>The generated history ends here (default: the clock's now) and spans <see cref="Span"/>.</summary>
        public long? EndMs { get; init; }
        public TimeSpan Span { get; init; } = TimeSpan.FromDays(730);
        public double ReplyRate { get; init; } = 0.15;
        public double EditRate { get; init; } = 0.03;
        public double DeleteRate { get; init; } = 0.01;
        public double ReactionRate { get; init; } = 0.2;
        public double MentionRate { get; init; } = 0.1;
        public double AttachmentRate { get; init; } = 0.05;
        public int BatchSize { get; init; } = 20_000;
    }

    public sealed record SyntheticCorpusResult(long Messages, int Persons, int Identities, int Conversations, long ElapsedMs);

    /// <summary>
    /// Generates a deterministic synthetic corpus straight into corpus.db, for the perf harness (plan: "5M-message
    /// synthetic corpus") and for workstreams to develop against without live data. Everything is invented: ids are
    /// prefixed <c>synth</c>, text comes from a small neutral vocabulary, and the generator refuses to write into a
    /// corpus that holds anything real (both repositories are public; real data never mixes with test data).
    /// <para>
    /// It is shaped to exercise the hard parts: people in different IANA zones with their own daily rhythm (local
    /// times resolved DST-safely), heavy-tailed activity, guilds, channels, DMs and WhatsApp groups (minute-precision,
    /// zone-inferred times), replies, edits with version history, deletions, reactions, mentions, attachments, bots,
    /// and non-ASCII text.
    /// </para>
    /// </summary>
    public static class SyntheticCorpus
    {
        public const string SourceId = "synthetic";

        private static readonly string[] ZoneIds =
            { "Europe/London", "America/New_York", "Europe/Berlin", "Asia/Tokyo", "Australia/Sydney", "America/Los_Angeles" };

        private static readonly string[] Words =
        {
            "alpha", "bravo", "cobalt", "delta", "ember", "fjord", "garnet", "harbor", "indigo", "juniper", "kestrel", "lumen", "meadow", "nimbus",
            "orbit", "pepper", "quartz", "river", "saffron", "tundra", "umber", "velvet", "willow", "xenon", "yarrow", "zephyr", "lantern", "pixel",
            "biscuit", "comet", "dune", "falcon", "glacier", "hazel", "island", "jigsaw", "koala", "lattice", "mosaic", "nectar", "otter", "puzzle",
            "café", "naïve", "über", "日本", "привет", "مرحبا", "😀", "🎉", "👍",
        };

        private static readonly string[] Emoji = { "👍", "😂", "❤️", "🎉", "😮", "🔥", "custom_wave:111", "custom_cat:222" };

        /// <summary>A valid 1×1 PNG: image modules get a decodable (if uninteresting) file for every synthetic image.</summary>
        private static readonly byte[] PlaceholderPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        /// <summary>Video/audio/file placeholders are deliberately undecodable: media modules must cope with bad files.</summary>
        private static readonly byte[] PlaceholderBlob = System.Text.Encoding.UTF8.GetBytes("omniscience synthetic placeholder (not real media)");

        private sealed record Person(string Id, string Identity, string? WhatsApp, TimeZoneInfo Zone, int PeakHour, double Weight, bool Bot);

        private sealed record Conversation(string Id, string Platform, string Kind, string? Guild, int[] Members);

        public static async Task<SyntheticCorpusResult> GenerateAsync(OmniscienceStores stores, IClock clock, SyntheticCorpusOptions o, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(o.People, 2);
            ArgumentOutOfRangeException.ThrowIfNegative(o.Messages);
            var timer = Stopwatch.StartNew();
            var rng = new Random(o.Seed);
            long end = o.EndMs ?? clock.NowMs;
            long start = end - (long)o.Span.TotalMilliseconds;

            using (var c = stores.Corpus.OpenRead())
            {
                long foreign = c.Scalar<long>(null, "SELECT COUNT(*) FROM sources WHERE kind <> 'synthetic'");
                long existing = c.Scalar<long>(null, "SELECT COUNT(*) FROM messages");
                if (foreign > 0 || existing > 0)
                    throw new InvalidOperationException("The synthetic corpus generator only writes into an empty corpus; this one already holds data.");
            }

            // People: heavy-tailed activity (a few talk a lot), each with a home zone and a peak hour.
            var zones = ZoneIds.Select(Zones.GetOrThrow).ToArray();
            var people = Enumerable.Range(0, o.People).Select(i => new Person(
                $"synth-p{i}", CorpusIds.Identity("discord", (100_000 + i).ToString(CultureInfo.InvariantCulture)),
                i % 3 == 0 ? CorpusIds.Identity("whatsapp", $"synth-wa-{i}") : null,
                zones[rng.Next(zones.Length)], rng.Next(24), 1.0 / Math.Pow(i + 1, 0.9), i % 50 == 49)).ToArray();

            var conversations = new List<Conversation>();
            for (int g = 0; g < o.Guilds; g++)
            {
                string guild = CorpusIds.Guild("discord", $"9000{g}");
                var members = Sample(rng, o.People, Math.Max(2, o.People / 2));
                for (int ch = 0; ch < o.ChannelsPerGuild; ch++)
                    conversations.Add(new Conversation(CorpusIds.Conversation("discord", $"800{g}{ch:D2}"), "discord", "guild_channel", guild, members));
            }
            for (int d = 0; d < o.DirectChats; d++)
                conversations.Add(new Conversation(CorpusIds.Conversation("discord", $"700{d:D4}"), "discord", "dm", null, Sample(rng, o.People, 2)));
            var waPeople = Enumerable.Range(0, o.People).Where(i => people[i].WhatsApp != null).ToArray();
            for (int w = 0; w < o.WhatsAppGroups && waPeople.Length >= 2; w++)
                conversations.Add(new Conversation(CorpusIds.Conversation("whatsapp", $"synth-chat-{w}"), "whatsapp", "group_dm", null,
                    waPeople.OrderBy(_ => rng.Next()).Take(Math.Min(waPeople.Length, 3 + rng.Next(6))).ToArray()));
            var convWeights = conversations.Select(cv => cv.Kind == "guild_channel" ? 3.0 : 1.0).ToArray();

            long now = clock.NowMs;
            await WriteStructureAsync(stores, people, conversations, start, now, ct).ConfigureAwait(false);
            var placeholders = await StorePlaceholdersAsync(stores, now, ct).ConfigureAwait(false);

            // Messages: draw (time, conversation, author), sort by time, then write in batches so replies point backwards.
            var drafts = new (long Time, int Conv, int Author)[o.Messages];
            var convCumulative = Cumulative(convWeights);
            for (long i = 0; i < o.Messages; i++)
            {
                int conv = Pick(rng, convCumulative);
                var cv = conversations[conv];
                int author = cv.Members[PickWeighted(rng, cv.Members, people)];
                drafts[i] = (LocalTime(rng, people[author], start, end, cv.Platform == "whatsapp"), conv, author);
            }
            Array.Sort(drafts, (a, b) => a.Time.CompareTo(b.Time));

            var recent = new Dictionary<int, List<string>>();
            int sequence = 0;
            for (long offset = 0; offset < drafts.LongLength; offset += o.BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                long upper = Math.Min(drafts.LongLength, offset + o.BatchSize);
                var batch = new List<Draft>((int)(upper - offset));
                for (long i = offset; i < upper; i++)
                {
                    var (time, conv, author) = drafts[i];
                    var cv = conversations[conv];
                    var p = people[author];
                    string messageId, platformId, authorIdentity;
                    long orderKey;
                    if (cv.Platform == "discord")
                    {
                        ulong snowflake = ((ulong)(time - Instant.DiscordEpochMs) << 22) | (uint)(sequence++ & 0x3FFFFF);
                        platformId = snowflake.ToString(CultureInfo.InvariantCulture);
                        orderKey = (long)snowflake;
                        authorIdentity = p.Identity;
                    }
                    else
                    {
                        platformId = $"synth-{i}";
                        orderKey = i;
                        authorIdentity = p.WhatsApp!;
                    }
                    messageId = CorpusIds.Message(cv.Platform, platformId);
                    recent.TryGetValue(conv, out var history);
                    string? replyTo = history is { Count: > 0 } && rng.NextDouble() < o.ReplyRate ? history[rng.Next(history.Count)] : null;
                    if (history == null) recent[conv] = history = new List<string>();
                    history.Add(messageId);
                    if (history.Count > 50) history.RemoveAt(0);

                    string text = Text(rng, i);
                    string? edited = rng.NextDouble() < o.EditRate ? text + " (edited " + Words[rng.Next(Words.Length)] + ")" : null;
                    long? editedAt = edited == null ? null : time + 60_000 + rng.Next(3_600_000);
                    long? deletedAt = rng.NextDouble() < o.DeleteRate ? time + 30_000 + rng.Next(86_400_000) : null;
                    string? mention = cv.Platform == "discord" && rng.NextDouble() < o.MentionRate ? people[cv.Members[rng.Next(cv.Members.Length)]].Identity : null;
                    var reactors = rng.NextDouble() < o.ReactionRate
                        ? cv.Members.OrderBy(_ => rng.Next()).Take(1 + rng.Next(Math.Min(4, cv.Members.Length))).Select(m => people[m].Identity).ToArray()
                        : Array.Empty<string>();
                    string? attachmentKind = rng.NextDouble() < o.AttachmentRate ? new[] { "image", "image", "video", "audio", "file" }[rng.Next(5)] : null;
                    // Most media in a real corpus is already downloaded; a minority is still pending.
                    bool stored = attachmentKind != null && rng.NextDouble() < 0.85;
                    batch.Add(new Draft(messageId, cv.Platform, platformId, cv.Id, cv.Guild, authorIdentity, p.Id, time, orderKey,
                        cv.Platform == "discord" ? TimeQuality.Exact : TimeQuality.InferredZone, p.Bot, replyTo, text, edited, editedAt, deletedAt,
                        mention, reactors, Emoji[rng.Next(Emoji.Length)], attachmentKind, stored));
                }
                long writtenAt = clock.NowMs;
                await stores.Corpus.WriteAsync((c, tx) => WriteMessages(c, tx, batch, placeholders, writtenAt), ct).ConfigureAwait(false);
            }

            int identities = people.Length + people.Count(p => p.WhatsApp != null);
            return new SyntheticCorpusResult(o.Messages, people.Length, identities, conversations.Count, timer.ElapsedMilliseconds);
        }

        private sealed record Draft(string MessageId, string Platform, string PlatformId, string Conversation, string? Guild, string Author, string Person,
            long Time, long OrderKey, TimeQuality Quality, bool Bot, string? ReplyTo, string Text, string? Edited, long? EditedAt, long? DeletedAt,
            string? Mention, string[] Reactors, string Emoji, string? AttachmentKind, bool AttachmentStored);

        private sealed record Placeholder(string Sha256, string Mime, long Size);

        /// <summary>Writes the placeholder media into v3's content-addressed store (once) and registers it.</summary>
        private static async Task<Dictionary<string, Placeholder>> StorePlaceholdersAsync(OmniscienceStores stores, long now, CancellationToken ct)
        {
            var result = new Dictionary<string, Placeholder>(StringComparer.Ordinal);
            var objects = new List<(Placeholder P, string Relative)>();
            foreach (var (kinds, bytes, mime) in new[] { (new[] { "image" }, PlaceholderPng, "image/png"), (new[] { "video", "audio", "file" }, PlaceholderBlob, "application/octet-stream") })
            {
                string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                string relative = $"{sha[..2]}/{sha.Substring(2, 2)}/{sha}";
                string path = MediaPaths.Resolve(stores.Paths, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                var placeholder = new Placeholder(sha, mime, bytes.Length);
                objects.Add((placeholder, relative));
                foreach (var k in kinds) result[k] = placeholder;
            }
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                foreach (var (p, relative) in objects)
                    c.Exec(tx, "INSERT OR IGNORE INTO cas_objects(sha256, size_bytes, mime, relative_path, stored_at_ms) VALUES($s,$b,$m,$r,$t)",
                        ("$s", p.Sha256), ("$b", p.Size), ("$m", p.Mime), ("$r", relative), ("$t", now));
            }, ct).ConfigureAwait(false);
            return result;
        }

        private static Task WriteStructureAsync(OmniscienceStores stores, Person[] people, List<Conversation> conversations, long start, long now, CancellationToken ct) =>
            stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT OR IGNORE INTO sources(source_id, platform, kind, label, status, rank, created_at_ms, updated_at_ms) VALUES($s,'synthetic','synthetic','Synthetic corpus','active',0,$n,$n)",
                    ("$s", SourceId), ("$n", now));
                foreach (var p in people)
                {
                    c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,$self)",
                        ("$p", p.Id), ("$t", start), ("$n", "Synthetic " + p.Id[7..]), ("$self", p.Id == "synth-p0" ? 1 : 0));
                    c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", p.Id));
                    ChangeLog.Append(c, tx, now, ChangeKinds.PersonCreate, p.Id, p.Id, null, SourceKinds.Import);
                    foreach (var identity in new[] { p.Identity, p.WhatsApp }.OfType<string>())
                    {
                        string platform = identity[..identity.IndexOf(':')];
                        c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($i,$pl,$u,$p,$k,$f,$l)",
                            ("$i", identity), ("$pl", platform), ("$u", CorpusIds.Native(identity)), ("$p", p.Id), ("$k", p.Bot ? "bot" : "user"), ("$f", start), ("$l", now));
                        c.Exec(tx, "INSERT INTO identity_names(identity_id, name_kind, value, valid_from_ms, source_kind) VALUES($i,'display',$v,$f,'import')",
                            ("$i", identity), ("$v", "Synthetic " + p.Id[7..]), ("$f", start));
                        ChangeLog.Append(c, tx, now, ChangeKinds.IdentityUpsert, identity, p.Id, null, SourceKinds.Import);
                    }
                }
                foreach (var guild in conversations.Select(cv => cv.Guild).OfType<string>().Distinct())
                {
                    c.Exec(tx, "INSERT INTO guilds(guild_id, platform, name, first_seen_ms, last_seen_ms) VALUES($g,'discord',$n,$f,$l)",
                        ("$g", guild), ("$n", "Synthetic guild " + CorpusIds.Native(guild)), ("$f", start), ("$l", now));
                    ChangeLog.Append(c, tx, now, ChangeKinds.GuildUpsert, guild, null, null, SourceKinds.Import);
                }
                foreach (var cv in conversations)
                {
                    c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, guild_id, title, first_seen_ms) VALUES($c,$p,$k,$g,$t,$f)",
                        ("$c", cv.Id), ("$p", cv.Platform), ("$k", cv.Kind), ("$g", cv.Guild), ("$t", "synthetic " + CorpusIds.Native(cv.Id)), ("$f", start));
                    foreach (int m in cv.Members)
                        c.Exec(tx, "INSERT OR IGNORE INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'member',$f)",
                            ("$c", cv.Id), ("$i", cv.Platform == "discord" ? people[m].Identity : people[m].WhatsApp), ("$f", start));
                    ChangeLog.Append(c, tx, now, ChangeKinds.ConversationUpsert, cv.Id, null, cv.Id, SourceKinds.Import);
                }
            }, ct);

        private static void WriteMessages(SqliteConnection c, SqliteTransaction tx, List<Draft> batch, Dictionary<string, Placeholder> placeholders, long now)
        {
            using var message = new PreparedStatement(c, tx, """
                INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, guild_id, author_identity_id, sent_at_ms, time_quality, order_key,
                    is_bot, reply_to_message_id, current_version, content, edited_at_ms, deleted_at_ms, first_source_kind, best_source_id, best_source_rank,
                    ingested_at_ms, ingest_seq)
                VALUES($id,$p,$pm,$c,$g,$a,$t,$q,$o,$bot,$r,$v,$content,$e,$d,'import',$src,0,$ing,$seq)
                """, "$id", "$p", "$pm", "$c", "$g", "$a", "$t", "$q", "$o", "$bot", "$r", "$v", "$content", "$e", "$d", "$src", "$ing", "$seq");
            using var change = new PreparedStatement(c, tx,
                "INSERT INTO change_log(at_ms, kind, entity_id, person_id, conversation_id, source_kind) VALUES($a,$k,$e,$p,$c,'import') RETURNING seq",
                "$a", "$k", "$e", "$p", "$c");
            using var version = new PreparedStatement(c, tx,
                "INSERT INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) VALUES($m,$v,$c,$e,$r,$s)", "$m", "$v", "$c", "$e", "$r", "$s");
            using var mention = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO message_mentions(message_id, target_kind, target_id) VALUES($m,'user',$t)", "$m", "$t");
            using var reaction = new PreparedStatement(c, tx, """
                INSERT OR IGNORE INTO reactions(message_id, emoji_key, emoji_name, identity_id, added_at_ms, time_quality, source_kind) VALUES($m,$e,$n,$i,$t,0,'import')
                """, "$m", "$e", "$n", "$i", "$t");
            using var attachment = new PreparedStatement(c, tx, """
                INSERT INTO attachments(attachment_id, message_id, idx, filename, mime, media_kind, size_bytes, cas_sha256, download_state, created_at_ms)
                VALUES($id,$m,0,$f,$mi,$k,$s,$cas,$st,$t)
                """, "$id", "$m", "$f", "$mi", "$k", "$s", "$cas", "$st", "$t");

            foreach (var d in batch)
            {
                long seq = Convert.ToInt64(change.ScalarRaw(now, ChangeKinds.MessageInsert, d.MessageId, d.Person, d.Conversation), CultureInfo.InvariantCulture);
                int versions = d.Edited == null ? 1 : 2;
                message.Run(d.MessageId, d.Platform, d.PlatformId, d.Conversation, d.Guild, d.Author, d.Time, (int)d.Quality, d.OrderKey, d.Bot ? 1 : 0,
                    d.ReplyTo, versions, d.Edited ?? d.Text, d.EditedAt, d.DeletedAt, SourceId, d.Time + 1000, seq);
                version.Run(d.MessageId, 1, d.Text, null, d.Time + 1000, SourceId);
                if (d.Edited != null) version.Run(d.MessageId, 2, d.Edited, d.EditedAt, d.EditedAt, SourceId);
                if (d.Mention != null) mention.Run(d.MessageId, d.Mention);
                foreach (var r in d.Reactors) reaction.Run(d.MessageId, d.Emoji, d.Emoji.Split(':')[0], r, d.Time + 5_000);
                if (d.AttachmentKind != null)
                {
                    var placeholder = placeholders[d.AttachmentKind];
                    attachment.Run(CorpusIds.Attachment(d.MessageId, 0), d.MessageId, $"synthetic.{d.AttachmentKind}", placeholder.Mime, d.AttachmentKind,
                        d.AttachmentStored ? placeholder.Size : 1024 + d.Text.Length, d.AttachmentStored ? placeholder.Sha256 : null,
                        d.AttachmentStored ? "done" : "pending", d.Time);
                }
            }
            foreach (var conv in batch.GroupBy(d => d.Conversation))
                c.Exec(tx, "UPDATE conversations SET last_message_ms=MAX(COALESCE(last_message_ms,0),$t) WHERE conversation_id=$c", ("$t", conv.Max(d => d.Time)), ("$c", conv.Key));
        }

        // ── sampling helpers ─────────────────────────────────────────────────────

        private static string Text(Random rng, long i)
        {
            int n = 2 + rng.Next(14);
            var words = new string[n];
            for (int k = 0; k < n; k++) words[k] = Words[rng.Next(Words.Length)];
            return $"synthetic message {i}: " + string.Join(' ', words);
        }

        /// <summary>A time in [start, end) whose local hour in the person's zone follows their daily rhythm; WhatsApp
        /// times are truncated to the minute like an export.</summary>
        private static long LocalTime(Random rng, Person p, long start, long end, bool minutePrecision)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                long day = start + (long)(rng.NextDouble() * (end - start));
                var localDate = DateOnly.FromDateTime(Zones.ToLocal(day, p.Zone));
                int hour = DiurnalHour(rng, p.PeakHour);
                var wall = new DateTime(localDate.Year, localDate.Month, localDate.Day, hour, rng.Next(60), rng.Next(60), rng.Next(1000), DateTimeKind.Unspecified);
                if (minutePrecision) wall = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, 0, DateTimeKind.Unspecified);
                long utc = Zones.ResolveLocal(wall, p.Zone).UtcMs;
                if (utc >= start && utc < end) return utc;
            }
            return start + (long)(rng.NextDouble() * (end - start));
        }

        private static int DiurnalHour(Random rng, int peak)
        {
            Span<double> w = stackalloc double[24];
            double total = 0;
            for (int h = 0; h < 24; h++)
            {
                int d = Math.Min(Math.Abs(h - peak), 24 - Math.Abs(h - peak));
                w[h] = 0.05 + Math.Exp(-(d * d) / 18.0);
                total += w[h];
            }
            double x = rng.NextDouble() * total;
            for (int h = 0; h < 24; h++)
            {
                x -= w[h];
                if (x <= 0) return h;
            }
            return peak;
        }

        private static int[] Sample(Random rng, int population, int count) =>
            Enumerable.Range(0, population).OrderBy(_ => rng.Next()).Take(Math.Min(population, count)).OrderBy(i => i).ToArray();

        private static double[] Cumulative(double[] weights)
        {
            var c = new double[weights.Length];
            double sum = 0;
            for (int i = 0; i < weights.Length; i++) c[i] = sum += weights[i];
            return c;
        }

        private static int Pick(Random rng, double[] cumulative)
        {
            double x = rng.NextDouble() * cumulative[^1];
            int i = Array.BinarySearch(cumulative, x);
            return Math.Min(i < 0 ? ~i : i, cumulative.Length - 1);
        }

        private static int PickWeighted(Random rng, int[] members, Person[] people)
        {
            double total = 0;
            foreach (int m in members) total += people[m].Weight;
            double x = rng.NextDouble() * total;
            for (int k = 0; k < members.Length; k++)
            {
                x -= people[members[k]].Weight;
                if (x <= 0) return k;
            }
            return members.Length - 1;
        }
    }
}
