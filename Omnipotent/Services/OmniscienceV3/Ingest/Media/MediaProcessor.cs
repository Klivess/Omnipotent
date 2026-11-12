using System.Globalization;
using System.Net;
using Omnipotent.Services.OmniscienceV3.Ingest.Imports;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>States of <c>media_objects.state</c>.</summary>
    public static class MediaObjectStates
    {
        public const string Ok = "ok";
        /// <summary>The bytes are damaged or are not what they claim to be.</summary>
        public const string Corrupt = "corrupt";
        /// <summary>A real file this build cannot look inside (HEIC, an image too large to decode).</summary>
        public const string Unsupported = "unsupported";
    }

    public sealed record DownloadOutcome(string State, string? Detail = null);

    public sealed record MediaSettings
    {
        /// <summary>Hosts attachments may be fetched from. Anything else is never requested.</summary>
        public IReadOnlyList<string> AllowedHosts { get; init; } = new[] { "cdn.discordapp.com", "media.discordapp.net" };
        public MediaLimits Limits { get; init; } = new();
        public int MaxAttempts { get; init; } = 5;
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Fetches attachments into the media store and reads what the bytes say (plan I-Media).
    /// <para><b>Downloading</b> only ever requests an https URL on an allow-listed host, without following
    /// redirects, with a size ceiling per kind. An expired link is recorded as unavailable — once, not retried
    /// forever. A server error is transient and retried by the job system.</para>
    /// <para><b>Probing</b> runs once per distinct content, however many messages carry it: size, animation,
    /// orientation, when it was taken, a perceptual hash and a thumbnail. Damaged files are quarantined: kept,
    /// marked, and never handed to OCR, captions or faces.</para>
    /// </summary>
    public sealed class MediaProcessor
    {
        private readonly OmniscienceStores stores;
        private readonly IClock clock;
        private readonly MediaStore media;
        private readonly Func<MediaSettings> settings;
        private readonly Func<TimeZoneInfo> klivesZone;
        private readonly HttpClient http;
        private readonly Func<IMediaLinkRefresher?> links;
        private readonly IAvProbe? av;

        /// <param name="links">Asks the source platform for a fresh link when one has expired (F403); null result = none can.</param>
        /// <param name="av">Reads the length and picture size of audio and video; null = not read.</param>
        public MediaProcessor(OmniscienceStores stores, IClock clock, MediaStore media, Func<MediaSettings> settings, Func<TimeZoneInfo> klivesZone, HttpMessageHandler? handler = null,
            Func<IMediaLinkRefresher?>? links = null, IAvProbe? av = null)
        {
            this.av = av;
            this.stores = stores;
            this.clock = clock;
            this.media = media;
            this.settings = settings;
            this.klivesZone = klivesZone;
            this.links = links ?? (() => null);
            // No automatic redirects: a redirect target has not been checked against the allow-list.
            http = handler != null
                ? new HttpClient(handler, disposeHandler: false)
                : new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(20) });
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // bounded per request below
        }

        /// <summary>Why a URL will not be requested, or null when it may be.</summary>
        public static string? RefusalFor(string? url, IReadOnlyList<string> allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "the attachment has no usable link";
            if (uri.Scheme != Uri.UriSchemeHttps) return "only https links are fetched";
            if (!string.IsNullOrEmpty(uri.UserInfo)) return "links carrying credentials are not fetched";
            if (!uri.IsDefaultPort) return "links to unusual ports are not fetched";
            string host = uri.IdnHost.ToLowerInvariant();
            bool allowed = allowedHosts.Any(a => host == a.Trim().ToLowerInvariant());
            return allowed ? null : $"{host} is not a host attachments are fetched from";
        }

        /// <summary>
        /// Downloads one pending attachment. Returns the state it ended in. Throws <see cref="JobFailureException"/>
        /// (transient) when the server or network failed and another try may work.
        /// </summary>
        public async Task<DownloadOutcome> DownloadAsync(string attachmentId, CancellationToken ct)
        {
            var cfg = settings();
            (bool Found, string State, string? Url, string Kind, string? Mime, int Attempts, string SourceKind, string? Person, string Conversation, string Platform,
                string PlatformMessageId, string? PlatformAttachmentId, int Index) row;
            using (var c = stores.Corpus.OpenRead())
                row = c.Query(null, """
                    SELECT a.download_state, a.source_url, a.media_kind, a.mime, a.download_attempts, m.first_source_kind, pc.canonical_id, m.conversation_id,
                           m.platform, m.platform_message_id, a.platform_attachment_id, a.idx
                    FROM attachments a JOIN messages m ON m.message_id=a.message_id
                    LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                    WHERE a.attachment_id=$a
                    """, r => (true, r.GetString(0), r.GetStringOrNull(1), r.GetString(2), r.GetStringOrNull(3), r.GetInt32(4), r.GetString(5), r.GetStringOrNull(6), r.GetString(7),
                        r.GetString(8), r.GetString(9), r.GetStringOrNull(10), r.GetInt32(11)),
                    ("$a", attachmentId)).FirstOrDefault();
            if (!row.Found) return new DownloadOutcome("missing", "no such attachment");
            if (row.State != DownloadStates.Pending) return new DownloadOutcome(row.State, "nothing to do");

            if (RefusalFor(row.Url, cfg.AllowedHosts) is { } refusal) return await FinishAsync(attachmentId, DownloadStates.Unavailable, refusal, ct).ConfigureAwait(false);

            string url = row.Url!;
            bool refreshed = false;
            HttpResponseMessage response;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(cfg.Timeout);
            while (true)
            {
                try
                {
                    response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    return await RetryOrFailAsync(attachmentId, row.Attempts, cfg, "the download did not complete: " + ex.Message, ct).ConfigureAwait(false);
                }
                int code = (int)response.StatusCode;
                if (refreshed || code is not (401 or 403 or 404 or 410)) break;

                // Platform media links are signed and expire (F403): the platform is asked once for a fresh link to the
                // same file, which is recorded on the attachment so a later try starts from it.
                response.Dispose();
                string? fresh;
                try
                {
                    fresh = await FreshLinkAsync(attachmentId, row.Platform, row.PlatformMessageId, row.Conversation, row.PlatformAttachmentId, row.Index, url, cfg, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return await RetryOrFailAsync(attachmentId, row.Attempts, cfg, $"the link has expired (HTTP {code}), and asking for a fresh one failed: " + ex.Message, ct).ConfigureAwait(false);
                }
                if (fresh == null)
                    return await FinishAsync(attachmentId, DownloadStates.Unavailable, $"the link has expired or was removed (HTTP {code})", ct).ConfigureAwait(false);
                url = fresh;
                refreshed = true;
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                // Platform media links expire: the server answers 403 or 404 and always will for this link.
                if (status is 401 or 403 or 404 or 410)
                    return await FinishAsync(attachmentId, DownloadStates.Unavailable,
                        refreshed ? $"the link had expired, and the fresh one was refused too (HTTP {status})" : $"the link has expired or was removed (HTTP {status})", ct).ConfigureAwait(false);
                if (status is >= 300 and < 400)
                    return await FinishAsync(attachmentId, DownloadStates.Unavailable, $"the link redirects elsewhere (HTTP {status}), which is not followed", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return await RetryOrFailAsync(attachmentId, row.Attempts, cfg, $"the server answered HTTP {status}", ct).ConfigureAwait(false);

                long limit = cfg.Limits.For(row.Kind);
                if (response.Content.Headers.ContentLength is { } declared && declared > limit)
                    return await FinishAsync(attachmentId, DownloadStates.TooLarge, new MediaTooLargeException(limit).Message, ct).ConfigureAwait(false);

                StoredObject stored;
                try
                {
                    await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    stored = await media.PutAsync(body, response.Content.Headers.ContentType?.MediaType ?? row.Mime, limit, timeout.Token).ConfigureAwait(false);
                }
                catch (MediaTooLargeException ex)
                {
                    return await FinishAsync(attachmentId, DownloadStates.TooLarge, ex.Message, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    return await RetryOrFailAsync(attachmentId, row.Attempts, cfg, "the download broke off: " + ex.Message, ct).ConfigureAwait(false);
                }

                long now = clock.NowMs;
                await stores.Corpus.WriteAsync((c, tx) =>
                {
                    MediaStore.Register(c, tx, stored, now);
                    int updated = c.Exec(tx, """
                        UPDATE attachments SET cas_sha256=$cas, download_state='done', download_error=NULL, download_attempts=download_attempts+1,
                            size_bytes=$n, mime=COALESCE($mi, mime)
                        WHERE attachment_id=$a AND download_state='pending'
                        """, ("$cas", stored.Sha256), ("$n", stored.SizeBytes), ("$mi", stored.Mime), ("$a", attachmentId));
                    // Tagged with how the message itself arrived, so media of a backfilled message is never treated as live.
                    if (updated == 1) ChangeLog.Append(c, tx, now, ChangeKinds.AttachmentStored, attachmentId, row.Person, row.Conversation, row.SourceKind);
                }, ct).ConfigureAwait(false);
                return new DownloadOutcome(DownloadStates.Done, stored.Sha256);
            }
        }

        /// <summary>A fresh link for an expired one from the source platform, recorded on the attachment; null when there is none.</summary>
        private async Task<string?> FreshLinkAsync(string attachmentId, string platform, string platformMessageId, string conversationId, string? platformAttachmentId, int index,
            string expired, MediaSettings cfg, CancellationToken ct)
        {
            if (links() is not { } source) return null;
            string? fresh = await source.RefreshAsync(new MediaLinkRequest(attachmentId, platform, platformMessageId, CorpusIds.Native(conversationId), platformAttachmentId, index, expired), ct)
                .ConfigureAwait(false);
            // Only a different link that passes the same checks as any other: a platform cannot point a download elsewhere.
            if (string.IsNullOrWhiteSpace(fresh) || fresh == expired || RefusalFor(fresh, cfg.AllowedHosts) != null) return null;
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE attachments SET source_url=$u WHERE attachment_id=$a AND download_state='pending'",
                ("$u", fresh), ("$a", attachmentId)), ct).ConfigureAwait(false);
            return fresh;
        }

        private async Task<DownloadOutcome> FinishAsync(string attachmentId, string state, string reason, CancellationToken ct)
        {
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE attachments SET download_state=$s, download_error=$e, download_attempts=download_attempts+1 WHERE attachment_id=$a AND download_state='pending'",
                ("$s", state), ("$e", reason), ("$a", attachmentId)), ct).ConfigureAwait(false);
            return new DownloadOutcome(state, reason);
        }

        private async Task<DownloadOutcome> RetryOrFailAsync(string attachmentId, int attemptsSoFar, MediaSettings cfg, string reason, CancellationToken ct)
        {
            if (attemptsSoFar + 1 >= cfg.MaxAttempts) return await FinishAsync(attachmentId, DownloadStates.Failed, reason, ct).ConfigureAwait(false);
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE attachments SET download_error=$e, download_attempts=download_attempts+1 WHERE attachment_id=$a", ("$e", reason), ("$a", attachmentId)), ct).ConfigureAwait(false);
            throw new JobFailureException(ErrorClass.Transient, reason);
        }

        /// <summary>
        /// Reads one stored object and records what it is. Idempotent: an object already probed by this version is
        /// left alone. Returns the object's state.
        /// </summary>
        public async Task<string> ProbeAsync(string sha256, long sourceSeq, CancellationToken ct)
        {
            (bool Found, string Path, long Size, string? Mime) cas;
            string kind;
            using (var c = stores.Corpus.OpenRead())
            {
                cas = c.Query(null, "SELECT relative_path, size_bytes, mime FROM cas_objects WHERE sha256=$s",
                    r => (true, r.GetString(0), r.GetInt64(1), r.GetStringOrNull(2)), ("$s", sha256)).FirstOrDefault();
                if (!cas.Found) return "missing";
                kind = c.Scalar<string>(null, "SELECT media_kind FROM attachments WHERE cas_sha256=$s LIMIT 1", ("$s", sha256)) ?? MediaKinds.Classify(cas.Mime, null);
            }
            using (var c = stores.Intel.OpenRead())
            {
                string? known = c.Scalar<string>(null, "SELECT state FROM media_objects WHERE sha256=$s", ("$s", sha256));
                if (known != null) return known;
            }

            string path = media.FullPath(cas.Path);
            long now = clock.NowMs;
            if (!File.Exists(path))
            {
                await RecordAsync(sha256, kind, cas, null, MediaObjectStates.Corrupt, "the stored file is missing from disk", null, now, sourceSeq, ct).ConfigureAwait(false);
                return MediaObjectStates.Corrupt;
            }

            // The kind is what the bytes are, not what the attachment was labelled.
            if (cas.Mime != null && cas.Mime.StartsWith("image/", StringComparison.Ordinal)) kind = MediaKinds.Image;
            if (kind is MediaKinds.Audio or MediaKinds.Voice or MediaKinds.Video && av != null) return await ProbeAvAsync(sha256, kind, cas, path, now, sourceSeq, ct).ConfigureAwait(false);
            if (kind != MediaKinds.Image)
            {
                await RecordAsync(sha256, kind, cas, null, MediaObjectStates.Ok, null, null, now, sourceSeq, ct).ConfigureAwait(false);
                return MediaObjectStates.Ok;
            }

            ImageFacts facts;
            try
            {
                facts = ImageProbe.Probe(path);
            }
            catch (ImageUnreadableException ex)
            {
                string state = ex.Unsupported ? MediaObjectStates.Unsupported : MediaObjectStates.Corrupt;
                await RecordAsync(sha256, kind, cas, null, state, ex.Message, null, now, sourceSeq, ct).ConfigureAwait(false);
                if (!ex.Unsupported)
                    await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx,
                        "UPDATE attachments SET download_state='quarantined', download_error=$e WHERE cas_sha256=$s AND download_state='done'", ("$e", ex.Message), ("$s", sha256)), ct).ConfigureAwait(false);
                return state;
            }

            var thumb = await media.PutAsync(new MemoryStream(facts.Thumbnail), facts.ThumbnailMime, 8L << 20, ct).ConfigureAwait(false);
            var (takenMs, takenQuality) = ResolveTaken(facts);
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                MediaStore.Register(c, tx, thumb, now);
                // Facts read from the bytes fill what the platform did not say; a platform-provided value is kept.
                c.Exec(tx, """
                    UPDATE attachments SET width=COALESCE(width,$w), height=COALESCE(height,$h),
                        taken_at_ms=COALESCE(taken_at_ms,$t), taken_quality=COALESCE(taken_quality,$q)
                    WHERE cas_sha256=$s
                    """, ("$w", facts.Width), ("$h", facts.Height), ("$t", takenMs), ("$q", takenMs == null ? null : (int)takenQuality), ("$s", sha256));
            }, ct).ConfigureAwait(false);
            await RecordAsync(sha256, kind, cas, facts, MediaObjectStates.Ok, null, thumb.Sha256, now, sourceSeq, ct).ConfigureAwait(false);
            return MediaObjectStates.Ok;
        }

        /// <summary>
        /// Audio and video: their length and picture size, read with ffprobe, fill what the platform did not say. A file
        /// ffprobe cannot read is marked unsupported, not damaged (it is still offered to transcription, which decides).
        /// Without ffprobe installed nothing is recorded, so a later probe can still read it.
        /// </summary>
        private async Task<string> ProbeAvAsync(string sha256, string kind, (bool Found, string Path, long Size, string? Mime) cas, string path, long now, long sourceSeq, CancellationToken ct)
        {
            AvFacts? facts;
            try
            {
                facts = await av!.ProbeAsync(path, ct).ConfigureAwait(false);
            }
            catch (ImageUnreadableException ex)
            {
                await RecordAsync(sha256, kind, cas, null, MediaObjectStates.Unsupported, ex.Message, null, now, sourceSeq, ct).ConfigureAwait(false);
                return MediaObjectStates.Unsupported;
            }
            if (facts == null) return "not read";
            // A "video" with no picture is sound (a voice clip sent as a video file), and is kept as the kind it was given.
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE attachments SET duration_ms=COALESCE(duration_ms,$d), width=COALESCE(width,$w), height=COALESCE(height,$h) WHERE cas_sha256=$s
                """, ("$d", facts.DurationMs), ("$w", facts.Width), ("$h", facts.Height), ("$s", sha256)), ct).ConfigureAwait(false);
            await stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO media_objects(sha256, media_kind, mime, size_bytes, width, height, frames, orientation, phash, taken_local, taken_offset_min,
                    thumb_sha256, state, error, probed_at_ms, source_seq)
                VALUES($s,$k,$m,$n,$w,$h,NULL,NULL,NULL,NULL,NULL,NULL,'ok',NULL,$t,$seq)
                ON CONFLICT(sha256) DO UPDATE SET media_kind=excluded.media_kind, width=excluded.width, height=excluded.height, state='ok', error=NULL,
                    probed_at_ms=excluded.probed_at_ms, source_seq=excluded.source_seq
                """, ("$s", sha256), ("$k", kind), ("$m", cas.Mime), ("$n", cas.Size), ("$w", facts.Width), ("$h", facts.Height), ("$t", now), ("$seq", sourceSeq)), ct)
                .ConfigureAwait(false);
            return MediaObjectStates.Ok;
        }

        /// <summary>
        /// When the picture was taken, as an instant. With the camera's own offset it is exact to the second.
        /// Without one, the wall clock is read in Klives's zone and marked inferred: the photographer's zone is
        /// unknown here (the person-zone model can refine it later from <c>media_objects.taken_local</c>).
        /// </summary>
        private (long? Ms, TimeQuality Quality) ResolveTaken(ImageFacts facts)
        {
            if (facts.TakenLocal == null
                || !DateTime.TryParseExact(facts.TakenLocal, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return (null, TimeQuality.Exact);
            long ms;
            TimeQuality quality;
            if (facts.TakenOffsetMinutes is { } offset)
            {
                ms = Instant.FromUtc(DateTime.SpecifyKind(wall.AddMinutes(-offset), DateTimeKind.Utc));
                quality = TimeQuality.Second;
            }
            else
            {
                ms = Zones.ResolveLocal(wall, klivesZone()).UtcMs;
                quality = TimeQuality.InferredZone;
            }
            return LegacyTime.IsPlausible(ms) ? (ms, quality) : (null, TimeQuality.Exact);
        }

        private Task RecordAsync(string sha256, string kind, (bool Found, string Path, long Size, string? Mime) cas, ImageFacts? facts, string state, string? error,
            string? thumbSha, long now, long sourceSeq, CancellationToken ct) =>
            stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO media_objects(sha256, media_kind, mime, size_bytes, width, height, frames, orientation, phash, taken_local, taken_offset_min,
                    thumb_sha256, state, error, probed_at_ms, source_seq)
                VALUES($s,$k,$m,$n,$w,$h,$f,$o,$p,$tl,$to,$th,$st,$e,$t,$seq)
                ON CONFLICT(sha256) DO UPDATE SET media_kind=excluded.media_kind, mime=excluded.mime, width=excluded.width, height=excluded.height,
                    frames=excluded.frames, orientation=excluded.orientation, phash=excluded.phash, taken_local=excluded.taken_local,
                    taken_offset_min=excluded.taken_offset_min, thumb_sha256=excluded.thumb_sha256, state=excluded.state, error=excluded.error,
                    probed_at_ms=excluded.probed_at_ms, source_seq=excluded.source_seq
                """,
                ("$s", sha256), ("$k", kind), ("$m", cas.Mime), ("$n", cas.Size), ("$w", facts?.Width), ("$h", facts?.Height), ("$f", facts?.Frames),
                ("$o", facts?.Orientation), ("$p", facts?.PerceptualHash), ("$tl", facts?.TakenLocal), ("$to", facts?.TakenOffsetMinutes), ("$th", thumbSha),
                ("$st", state), ("$e", error), ("$t", now), ("$seq", sourceSeq)), ct);
    }
}
