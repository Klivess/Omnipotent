using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Imports
{
    [ApiContract]
    public sealed record ImportPreviewConversation(string ConversationId, string? Title, string Kind, int Messages);

    [ApiContract]
    public sealed record ImportPreviewSender(string Name, string IdentityId, int Messages, bool IsOwner);

    [ApiContract]
    public sealed record ImportUnreadableFile(string File, string Reason);

    /// <summary>What an export holds and what must be decided before it is committed. Nothing has been written.</summary>
    [ApiContract]
    public sealed record ImportPreview
    {
        public required string Name { get; init; }
        public required string Format { get; init; }
        public required string FormatTitle { get; init; }
        public required string Sha256 { get; init; }
        public int Messages { get; init; }
        public int SystemMessages { get; init; }
        public long? FirstMs { get; init; }
        public long? LastMs { get; init; }
        /// <summary>Messages of this export already in the corpus (from an earlier import or another source).</summary>
        public int AlreadyInCorpus { get; init; }
        /// <summary>When this exact export was last committed, if ever. Committing again only changes what differs.</summary>
        public long? CommittedBeforeMs { get; init; }
        public IReadOnlyList<ImportPreviewConversation> Conversations { get; init; } = Array.Empty<ImportPreviewConversation>();
        /// <summary>The most active senders (at most 200).</summary>
        public IReadOnlyList<ImportPreviewSender> Senders { get; init; } = Array.Empty<ImportPreviewSender>();
        public int SenderCount { get; init; }
        /// <summary>The zone wall-clock times were read in; null when the export carries instants.</summary>
        public string? ZoneUsed { get; init; }
        /// <summary>"day-first", "month-first", "year-first" or "ambiguous"; null when the format has no numeric dates.</summary>
        public string? DateOrder { get; init; }
        public int AmbiguousTimes { get; init; }
        public int InvalidTimes { get; init; }
        public int MediaFound { get; init; }
        public int MediaMissing { get; init; }
        public int RepairedText { get; init; }
        public int SkippedRecords { get; init; }
        public string? OwnerDetected { get; init; }
        public IReadOnlyList<ImportUnreadableFile> UnreadableFiles { get; init; } = Array.Empty<ImportUnreadableFile>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        /// <summary>Set when the export cannot be read until Klives decides something (zone, date order, columns).</summary>
        public string? DecisionNeeded { get; init; }
    }

    [ApiContract]
    public sealed record ImportResult
    {
        public required string ImportId { get; init; }
        public required string Format { get; init; }
        public required string Sha256 { get; init; }
        public int Read { get; init; }
        public int Inserted { get; init; }
        public int Updated { get; init; }
        public int Unchanged { get; init; }
        public int Rejected { get; init; }
        public int NewIdentities { get; init; }
        public int NewConversations { get; init; }
        public int AttachmentsAdded { get; init; }
        public int AttachmentsStored { get; init; }
        public int MediaTooLarge { get; init; }
        public int MediaFailed { get; init; }
        public int SkippedRecords { get; init; }
        public IReadOnlyList<ImportUnreadableFile> UnreadableFiles { get; init; } = Array.Empty<ImportUnreadableFile>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    /// <summary>Per-kind ceilings for media taken from an export. Larger files are recorded, not stored.</summary>
    public sealed record MediaLimits(long ImageBytes = 64L << 20, long VideoBytes = 1L << 30, long AudioBytes = 256L << 20, long FileBytes = 256L << 20)
    {
        public long For(string kind) => kind switch
        {
            MediaKinds.Image => ImageBytes,
            MediaKinds.Video => VideoBytes,
            MediaKinds.Audio or MediaKinds.Voice => AudioBytes,
            _ => FileBytes,
        };
    }

    /// <summary>
    /// Imports chat exports (plan I-Importers): detect the format, preview without writing, then commit.
    /// <list type="bullet">
    /// <item><b>Idempotent.</b> The ledger knows each export by its content hash, and message ids are derived from the
    /// export's own content, so committing the same export twice — or a longer export of the same chat later —
    /// changes only what is new. Committing again with a corrected zone or date order corrects the stored times.</item>
    /// <item><b>Bounded memory.</b> Messages stream through in batches; media is streamed to the content store.</item>
    /// <item><b>Partial failure keeps the rest.</b> An unreadable file, record or media item is reported and skipped.</item>
    /// <item><b>Never live.</b> Everything is written with source kind <c>import</c>, so nothing imported can raise an alert.</item>
    /// </list>
    /// </summary>
    public sealed class ImportService
    {
        public const int BatchSize = 500;

        public static IReadOnlyList<IImportFormat> Formats { get; } = new IImportFormat[]
        {
            new DiscordPackageImport(), new WhatsAppImport(), new TelegramImport(), new MetaImport("instagram"), new MetaImport("messenger"), new TableImport(),
        };

        private readonly OmniscienceStores stores;
        private readonly IClock clock;
        private readonly MediaStore media;
        private readonly Func<string> klivesZone;
        private readonly MediaLimits limits;

        public ImportService(OmniscienceStores stores, IClock clock, MediaStore media, Func<string> klivesZone, MediaLimits? limits = null)
        {
            this.stores = stores;
            this.clock = clock;
            this.media = media;
            this.klivesZone = klivesZone;
            this.limits = limits ?? new MediaLimits();
        }

        /// <summary>The format named, or the one most sure the input is its own.</summary>
        public static IImportFormat Detect(ImportInput input, string? formatId)
        {
            if (!string.IsNullOrWhiteSpace(formatId))
                return Formats.FirstOrDefault(f => f.Id.Equals(formatId.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidDataException($"There is no import format '{formatId}'. Known: {string.Join(", ", Formats.Select(f => f.Id))}.");
            var best = Formats.Select(f => (Format: f, Score: f.Detect(input))).OrderByDescending(x => x.Score).First();
            if (best.Score < 0.3)
                throw new InvalidDataException("This does not look like any export Omniscience can read (WhatsApp, Telegram, Instagram, Messenger, a Discord data package, or a CSV/JSON table).");
            return best.Format;
        }

        /// <summary>Times without a zone default to Klives's own, shown in the preview so it can be changed before commit.</summary>
        private ImportOptions WithDefaults(ImportOptions options) => options.Zone != null ? options : options with { Zone = klivesZone() };

        public ImportPreview Preview(string path, string? formatId, ImportOptions options, CancellationToken ct = default)
        {
            using var input = ImportInput.Open(path);
            var format = Detect(input, formatId);
            options = WithDefaults(options);
            string sha = input.ContentSha256();
            var diagnostics = new ImportDiagnostics();

            int messages = 0, system = 0, known = 0;
            long? first = null, last = null;
            string? decision = null;
            var conversations = new Dictionary<string, (string? Title, string Kind, int Count)>(StringComparer.Ordinal);
            var senders = new Dictionary<string, (string Name, int Count, bool Owner)>(StringComparer.Ordinal);
            var ids = new List<string>(BatchSize);

            void CountKnown()
            {
                if (ids.Count == 0) return;
                using var c = stores.Corpus.OpenRead();
                known += c.QueryIn(null, "SELECT message_id FROM messages WHERE message_id IN ({keys})", ids, r => r.GetString(0)).Count;
                ids.Clear();
            }

            try
            {
                foreach (var m in format.Read(input, options, diagnostics))
                {
                    ct.ThrowIfCancellationRequested();
                    messages++;
                    if (m.IsSystem) system++;
                    first = first == null ? m.SentAtMs : Math.Min(first.Value, m.SentAtMs);
                    last = last == null ? m.SentAtMs : Math.Max(last.Value, m.SentAtMs);

                    string conversationId = CorpusIds.Conversation(m.Conversation.Platform, m.Conversation.ChannelId);
                    conversations.TryGetValue(conversationId, out var cv);
                    conversations[conversationId] = (m.Conversation.Title ?? cv.Title, m.Conversation.Kind, cv.Count + 1);
                    if (!m.IsSystem)
                    {
                        string identityId = CorpusIds.Identity(m.Author.Platform, m.Author.PlatformUserId);
                        senders.TryGetValue(identityId, out var s);
                        senders[identityId] = (m.Author.DisplayName ?? m.Author.Username ?? s.Name ?? m.Author.PlatformUserId, s.Count + 1, s.Owner || m.Author.IsSelf);
                    }
                    ids.Add(CorpusIds.Message(m.Conversation.Platform, m.PlatformMessageId));
                    if (ids.Count >= BatchSize) CountKnown();
                }
                CountKnown();
            }
            catch (ImportDecisionNeededException ex)
            {
                decision = ex.Message;
            }

            long? committedBefore;
            using (var c = stores.Corpus.OpenRead())
                committedBefore = c.Scalar<long?>(null, "SELECT committed_at_ms FROM import_ledger WHERE file_sha256=$s AND state='committed'", ("$s", sha));

            return new ImportPreview
            {
                Name = input.Name,
                Format = format.Id,
                FormatTitle = format.Title,
                Sha256 = sha,
                Messages = messages,
                SystemMessages = system,
                FirstMs = first,
                LastMs = last,
                AlreadyInCorpus = known,
                CommittedBeforeMs = committedBefore,
                Conversations = conversations.OrderByDescending(c => c.Value.Count).Take(500)
                    .Select(c => new ImportPreviewConversation(c.Key, c.Value.Title, c.Value.Kind, c.Value.Count)).ToList(),
                Senders = senders.OrderByDescending(s => s.Value.Count).ThenBy(s => s.Value.Name, StringComparer.Ordinal).Take(200)
                    .Select(s => new ImportPreviewSender(s.Value.Name, s.Key, s.Value.Count, s.Value.Owner)).ToList(),
                SenderCount = senders.Count,
                ZoneUsed = diagnostics.ZoneUsed,
                DateOrder = diagnostics.DateOrder,
                AmbiguousTimes = diagnostics.AmbiguousTimes,
                InvalidTimes = diagnostics.InvalidTimes,
                MediaFound = diagnostics.MediaFound,
                MediaMissing = diagnostics.MediaMissing,
                RepairedText = diagnostics.RepairedText,
                SkippedRecords = diagnostics.SkippedRecords,
                OwnerDetected = diagnostics.OwnerDetected,
                UnreadableFiles = diagnostics.UnreadableFiles.Select(f => new ImportUnreadableFile(f.File, f.Reason)).ToList(),
                Warnings = diagnostics.Warnings.ToList(),
                DecisionNeeded = decision,
            };
        }

        public async Task<ImportResult> CommitAsync(string path, string? formatId, ImportOptions options, Func<ImportResult, Task>? progress = null, CancellationToken ct = default)
        {
            using var input = ImportInput.Open(path);
            var format = Detect(input, formatId);
            options = WithDefaults(options);
            string sha = input.ContentSha256();
            string importId = "imp_" + sha[..20];
            string sourceId = $"import:{format.Id}:{sha[..20]}";
            var diagnostics = new ImportDiagnostics();

            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO import_ledger(import_id, format, file_sha256, zone, state, created_at_ms) VALUES($i,$f,$s,$z,'running',$n)
                ON CONFLICT(file_sha256) DO UPDATE SET state='running', zone=excluded.zone, error=NULL
                """, ("$i", importId), ("$f", format.Id), ("$s", sha), ("$z", options.Zone), ("$n", clock.NowMs)), ct).ConfigureAwait(false);

            var totals = new IngestStats();
            int read = 0, tooLarge = 0, failed = 0;
            ImportResult Snapshot() => new()
            {
                ImportId = importId,
                Format = format.Id,
                Sha256 = sha,
                Read = read,
                Inserted = totals.Inserted,
                Updated = totals.Updated,
                Unchanged = totals.Unchanged,
                Rejected = totals.Rejected,
                NewIdentities = totals.NewIdentities,
                NewConversations = totals.NewConversations,
                AttachmentsAdded = totals.AttachmentsAdded,
                AttachmentsStored = totals.AttachmentsStored,
                MediaTooLarge = tooLarge,
                MediaFailed = failed,
                SkippedRecords = diagnostics.SkippedRecords,
                UnreadableFiles = diagnostics.UnreadableFiles.Select(f => new ImportUnreadableFile(f.File, f.Reason)).ToList(),
                Warnings = diagnostics.Warnings.ToList(),
            };

            try
            {
                var batch = new List<IngestMessage>(BatchSize);
                async Task FlushAsync()
                {
                    if (batch.Count == 0) return;
                    // Files first, outside the writer: the corpus transaction only ever touches the database.
                    var prepared = new List<IngestMessage>(batch.Count);
                    foreach (var m in batch)
                    {
                        if (m.Attachments.All(a => a.LocalFile == null)) { prepared.Add(m); continue; }
                        var stored = new List<IngestAttachment>(m.Attachments.Count);
                        foreach (var a in m.Attachments)
                        {
                            var (result, outcome) = await StoreAsync(a, ct).ConfigureAwait(false);
                            if (outcome == DownloadStates.TooLarge) tooLarge++;
                            else if (outcome == DownloadStates.Failed) failed++;
                            stored.Add(result);
                        }
                        prepared.Add(m with { Attachments = stored });
                    }

                    string platform = prepared[0].Conversation.Platform.Trim().ToLowerInvariant();
                    var source = new IngestSource(sourceId, platform, "import", $"{format.Title}: {input.Name}", IngestSource.ImportRank);
                    long now = clock.NowMs;
                    var stats = await stores.Corpus.WriteAsync((c, tx) =>
                    {
                        using var writer = new CorpusWriter(c, tx, source, SourceKinds.Import, now);
                        foreach (var m in prepared) writer.Message(m);
                        return writer.Complete();
                    }, ct).ConfigureAwait(false);
                    totals += stats;
                    read += batch.Count;
                    batch.Clear();
                    if (progress != null) await progress(Snapshot()).ConfigureAwait(false);
                }

                foreach (var m in format.Read(input, options, diagnostics))
                {
                    ct.ThrowIfCancellationRequested();
                    batch.Add(m);
                    if (batch.Count >= BatchSize) await FlushAsync().ConfigureAwait(false);
                }
                await FlushAsync().ConfigureAwait(false);

                var done = Snapshot();
                await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx,
                    "UPDATE import_ledger SET state='committed', counts_json=$j, committed_at_ms=$n, error=NULL WHERE file_sha256=$s",
                    ("$j", JsonSerializer.Serialize(done, KernelJson.Options)), ("$n", clock.NowMs), ("$s", sha)), ct).ConfigureAwait(false);
                return done;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // What was written stays (it is valid, and a retry skips it); the ledger says the import did not finish.
                string state = ex is ImportDecisionNeededException ? "needs_decision" : "failed";
                await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE import_ledger SET state=$st, counts_json=$j, error=$e WHERE file_sha256=$s",
                    ("$st", state), ("$j", JsonSerializer.Serialize(Snapshot(), KernelJson.Options)), ("$e", ex.Message), ("$s", sha)), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Puts an export's own media file into the store. A file that cannot be stored is recorded with the reason.</summary>
        private async Task<(IngestAttachment Attachment, string Outcome)> StoreAsync(IngestAttachment a, CancellationToken ct)
        {
            if (a.LocalFile == null) return (a, "");
            string kind = a.MediaKind ?? MediaKinds.Classify(a.Mime, a.Filename);
            try
            {
                await using var stream = a.LocalFile();
                var stored = await media.PutAsync(stream, a.Mime, limits.For(kind), ct).ConfigureAwait(false);
                return (a with { Stored = stored, LocalFile = null }, DownloadStates.Done);
            }
            catch (MediaTooLargeException ex)
            {
                return (a with { LocalFile = null, NotStored = (DownloadStates.TooLarge, ex.Message) }, DownloadStates.TooLarge);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return (a with { LocalFile = null, NotStored = (DownloadStates.Failed, ex.Message) }, DownloadStates.Failed);
            }
        }
    }
}
