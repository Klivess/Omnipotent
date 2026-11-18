# WS-A — Ingest & media (progress)

Branch `claude/omniscience-v3-ws-a-878bce` (worktree `.claude/worktrees/omniscience-v3-ws-a-878bce`), cut from Phase 1 (ac223e6).
Charter: [workstreams.md](workstreams.md). The workstream's original session produced no commits; since 2026-11-10 the
coordinating session does this work itself, in this worktree. This file is kept current so any session can resume.

## Layout (`Omnipotent/Services/OmniscienceV3/Ingest/`)
| Path | What |
|---|---|
| `IngestModel.cs` | The normalised records every source produces: `IngestMessage`, `IngestIdentity`, `IngestConversation`, `IngestAttachment`, `IngestReaction`, `IngestSource`, `IngestStats`. |
| `CorpusWriter.cs` | **The one write path into corpus.db.** Natural-key upserts, versions instead of overwrites, source-ranked merges, name validity intervals, one change-log row per mutation. |
| `CorpusWriter.Events.cs` | The same writer for what is not a message: deletions, reactions given/taken back/cleared, typing, presence (stored when it changes), voice sessions (open, move, close, reconcile), membership and nickname changes, channel and server metadata. |
| `Discord/DiscordPayload.cs` | Discord's own JSON (gateway and bot API share a shape) → `IngestMessage` / `IngestIdentity` / `DiscordChannelInfo`. Pure. |
| `Discord/DiscordBotApi.cs` | Read-only client for Discord's bot API: per-route allowances from response headers, 429 waits, retries, classified refusals (`DiscordApiException.Failure`). `IDiscordBotApi` is the seam tests replace. |
| `Discord/DiscordHistory.cs` | Reads a channel forward (live traffic, gaps) and backward (history) with a cursor per channel in `ingest_cursors`; re-fetches a changed message; writes relayed copies. |
| `Discord/BotFeed.cs` | `IBotFeed` and the `BotEvent` records: KliveBot's connection without the Discord library's types. |
| `Discord/KliveBotFeed.cs` | The one file that touches DSharpPlus: listens to the client the KliveBot service already holds. |
| `Discord/DiscordBotModule.cs` | Module `ingest.discord_bot`: the event pump and the `pull` / `backfill` / `discover` / `threads` jobs. |
| `Media/MediaStore.cs` | Content-addressed store (`media/ab/cd/<sha256>`), streamed and hashed, type sniffed from the bytes, `DownloadStates`. |
| `Media/ImageProbe.cs` | One read of an image: upright size, frames, EXIF taken-time (never GPS), perceptual hash, WebP thumbnail; `LoadFrames` for vision steps. |
| `Media/MediaProcessor.cs` | Downloading (allow-listed https hosts, no redirects, size ceilings) and probing (once per distinct content, quarantine of damaged files). |
| `Media/MediaModule.cs` | Module `media.store`: download and probe jobs from the change log, a sweep for media that predates the module. Intel table `media_objects`. |
| `Media/MediaText.cs` | Text from media: `MediaTextProcessor` (once per content, cached, labels per attachment), `TesseractOcr`, `FfmpegSegmenter`, `AiRouterTranscriber`, `TranscriptMerge`. |
| `Media/MediaTextModule.cs` | Module `media.text`: an `extract` job per stored attachment, a sweep for what was missed. Intel table `media_text`. |
| `Imports/ImportContracts.cs` | `ImportInput` (file, zip or folder), `ImportOptions`, `IImportFormat`, `TextDecoding` (BOMs, UTF-16, mojibake repair), `JsonPullReader` (streaming JSON), `ImportIds`. |
| `Imports/*Import.cs` | Formats: `WhatsAppImport`, `TelegramImport`, `MetaImport` (Instagram, Messenger), `DiscordPackageImport`, `TableImport` (CSV/TSV/JSON). |
| `Imports/ImportService.cs` | Detect → preview (no writes) → commit (idempotent, batched, media into the store). |
| `Imports/ImportModule.cs` | Module `ingest.imports`: preview and commit as kernel jobs on exports in the inbox folder; a scan that previews exports dropped there. |
| `../Api/IngestRoutes.cs` | Routes below. |

## Modules
| Module | Jobs | Notes |
|---|---|---|
| `ingest.imports` | `preview`, `commit`, `scan` | Lane Io. Single-flight per export; `MaxAttempts = 1` (a failed import is retried by Klives after he fixes what it named). `scan` (every 5 min) previews inbox entries nobody has looked at yet, once they have been untouched for two minutes. |
| `ingest.discord_bot` | `pull`, `backfill`, `discover`, `threads` | Lane Io. **Off by default** (`ingest.discord_bot.enabled`). `pull` (per channel, priority 900 when a doorbell rang) reads forward; `backfill` (per channel) reads backward, `backfill_pages_per_run` pages a run; `discover` (every 30 min and whenever the connection comes up) queues a forward read for every channel and a history job for those not read to their start; `threads` (daily) lists archived threads and forum posts. Implements `IAsyncDisposable`: it owns the event pump. |
| `media.store` | `download`, `probe`, `sweep` | Subscribes to `attachment.add` and `attachment.stored`. Sweep every 10 min. |
| `media.text` | `extract`, `sweep` | Depends on `media.store`; subscribes to `attachment.stored`. Sweep every 15 min. Lane Io. |

## Routes (all under `/omniscience/v3/`)
| Route | Permission | What |
|---|---|---|
| `GET imports` | `sources.view` | `ImportListDto`: the inbox folder's entries, the ledger, the known formats. |
| `POST imports/preview` | `sources.manage` | Body `ImportJobPayload {name, format?, options?}` → `ImportJobStartedDto {jobId}`. The job's `resultJson` is an `ImportPreview`. |
| `POST imports/commit` | `sources.manage` | Same body → job whose `resultJson` is an `ImportResult`. |
| `PUT imports/upload?name=<file name>` | `sources.manage` | The export as the request body, streamed to disk (limit 32 GB). It appears in the inbox only once complete, and a preview job is queued for it: `ImportUploadedDto {name, bytes, previewJobId}`. 400 when the name is not a plain file name or is taken. |
| `GET sources/discord` | `sources.view` | `DiscordBotStatusDto`: enabled, attached, the bot account, whether Discord gives it message text, source status, event counters, channel counts by state. |
| `POST sources/discord/backfill` | `sources.manage` | Body `DiscordBackfillRequest {channelId}` → `ImportJobStartedDto {jobId}`: read one channel back to its start now. |
| `GET media/file?attachment=<id>` | `messages.read` | The attachment's stored bytes. 404 `not_stored` when there are none. |
| `GET media/thumb?attachment=<id>` | `messages.read` | Its WebP thumbnail (max side 320). 404 `not_built` when not an image or not yet probed. |

## What other workstreams read from here
- **Source kinds on Discord rows:** a message read within `ingest.discord_bot.live_window_seconds` (default 300) of being sent is
  `live`; anything older is `backfill`, including the gap read after downtime. An edit or deletion of a message already held is
  logged `live` whatever the message's age (it happened now).
- **Events:** `presence_events` has one row per *change* per account (`activities_json` and `client_status_json` in Discord's
  field names; `dedupe_key` ends in the change fingerprint). `voice_sessions.left_reason` is `left`, `moved` or `unseen` (found
  gone after a gap: the end time is an upper bound). `member_events.kind` is `join` / `leave` / `update` with `detail_json`
  (`nick_before/after`, `roles_added/removed`, `timeout_until_ms`). Reaction times from the bot are when the event was heard
  (`time_quality` = Second).
- **Accounts that were only seen:** presence, typing, voice and membership rows carry an `identity_id` that may have **no row in
  `identities` and no person** (a silent server member). Their names are in `identity_names` all the same. The rows link up by
  id once the account writes or reacts. Join with `LEFT JOIN identities`.
- **Names:** `identity_names.name_kind` is `username`, `display` (Discord's global display name), or `nick:<server id>` (one
  timeline per server). `identity_avatars` holds the avatar hash history (files are not downloaded yet).
- **Conversation kinds:** `dm`, `group_dm`, `text`, `thread` (with `parent_conversation_id`), `channel` (announcements), plus
  `guild_channel` on rows migrated from v2 that no better source has named yet (`ConversationKinds.IsVague`).
- **Cursors:** `ingest_cursors` for `source_id='discord:klivebot'`: everything between `oldest_platform_id` and
  `newest_platform_id` has been read; `backward_complete=1` means back to the first message; `state` is `ok`, or
  `forbidden|<why>` / `gone|<why>`.
- **Media text (WS-B's evidence reader):** `labels` rows with `item_kind='attachment'`, `item_id` = attachment id, `task` = `ocr` or
  `transcript`, `signals_json` = `{ text, language, origin, sha256 }` where `origin` is `image_text` or `speech` (quoted material,
  never the poster's own words). An empty `text` with `abstained=1` means "read, nothing there". `model_version` changes when
  the engine or its language data does; older labels stay beside the new ones. Task `caption` (origin `ai_caption`) is
  written by `media.caption`: one or two neutral sentences, a kind and a few tags per stored image.
- **Media facts:** intel `media_objects` (by content hash): upright size, frames, orientation, `phash`, `taken_local`,
  `thumb_sha256`, `state` (`ok` / `corrupt` / `unsupported`).
- **Frames for vision:** `ImageProbe.LoadFrames(path, maxFrames)` gives upright bitmaps (three spread frames of an animation).

## Answers to WS-E's questions (ws-e.md contract gaps)
- **How files reach the importers:** `PUT imports/upload?name=…` with the file as the raw body (not multipart), or the **inbox
  folder** (`SavedData/Omniscience/v3/imports/`) for anything larger than 32 GB or an unpacked folder. Either way the entry is
  previewed automatically; `imports/preview` / `imports/commit` take a *name inside the inbox*, never a path, so the API cannot
  be pointed at arbitrary files.
- **How media is served:** `media/file` and `media/thumb` by attachment id, above.
- **Face boxes:** not built yet. When they are, boxes will be fractions (0–1) of the **upright** image, the same image
  `media/file` shows once the viewer applies no rotation of its own (thumbnails are already upright).

## Decisions (recorded so they can be revisited)
- **Discord: the official bot gateway only.** The plan lists a user-token gateway (I-DiscordUser). It is **not built** in v3 and
  this workstream will not build it: the guardrail recorded with the approval is that v3 "uses only the official Discord bot
  gateway and adds nothing to disguise automation from Discord", and automating a user account is against Discord's terms.
  Klives's own history comes in through Discord's data package (the `discord-package` importer) and, until cutover, through the
  v2 tail. KliveBot (the bot gateway) is the live source. **This narrows the plan; it is Klives's call to reopen.**
- **A gateway event is a doorbell, not the message.** The Discord library v3 shares with KliveBot (DSharpPlus 4.5.2) does not
  expose the payload and its own objects lack display names, forwards, polls and voice-note lengths. So an event only says
  which channel has something new; the message is read through the bot API, forward from the channel's cursor. One path then
  serves live traffic, gaps after downtime and ordering. The library's thin copy is written (source `discord:klivebot:relay`,
  rank 15) only where the bot can see a channel but may not read its history, and is brought up to the full payload if access
  is granted later.
- **v3 opens no Discord connection.** It attaches to the client the KliveBot service holds, re-attaches if that client is
  replaced, and detaches when switched off. The bot API calls use the bot's own token, read from the bot's token file per
  request, sent only to `discord.com`, never logged or stored.
- **Live capture is off by default** (`ingest.discord_bot.enabled=false`), like downloading: in shadow mode v2 captures and the
  legacy tail brings it across. The legacy tail also overwrites a conversation's title and kind with v2's on every pass, so the
  two should not run together for long. Turn it on at cutover.
- **A profile on an old message is today's profile.** Discord's API puts the author's current name and avatar on every message.
  They are recorded as of the fetch (`IngestIdentity.ObservedAtMs`), not the message. The only name ever known for an account
  is taken to reach back to its oldest message; once a second name is known, old messages move nothing.
- **Being seen is not taking part.** Presence, typing, voice and membership do not create an identity row or a person; writing
  and reacting do (F397).
- **Merge rules added:** text beats no text whatever the source rank, and empty never blanks text (F458); the platform's own
  payload (rank 20) replaces type, flags, forward, embeds and `raw_json` recorded by a poorer copy **without** a new version or
  a `message.version` change (the text is the same, derived work is not redone).
- **Empty text without the content intent is unknown** (`content` NULL), except in DMs, for the bot's own messages and where the
  bot is mentioned — the cases in which Discord does deliver text.
- **Person ids are derived from the first identity** (`CorpusWriter.PersonIdFor`), not random, so re-importing into a rebuilt
  corpus yields the same ids and gold labels that reference them still apply.
- **Message ids for formats without ids** hash the conversation, sender, the timestamp *text as written*, the content and the
  position among identical messages (`ImportIds.Message`). Re-importing with a corrected zone or date order therefore lands on
  the same rows and corrects their times (`CorpusWriter` replaces an `InferredZone` time, never a platform instant).
- **Content conflicts:** a newer edit time wins; without edit times a higher `sources.rank` wins (import 10, platform 20, v2 0);
  otherwise the stored content stays. Content `null` never blanks stored text.
- **Media derived from a message keeps the message's source kind** in the change log (media of a backfilled message is backfill).
- **Downloading is off by default** (`media.store.download_enabled=false`) while v2 still fetches attachments, so nothing is
  fetched twice during the shadow run. Turn it on at cutover.
- **Meta exports:** a sender is scoped to its thread (`thread:<thread>:<name>`) until linked; the account owner is one identity
  (`self:<name>`). The owner is detected as the one name present in every thread (needs two threads), or given as `ownerName`.
- **WhatsApp:** a sender name is how Klives's phone labelled someone in that export, so it identifies a person **within that
  chat** (`name:<chat>:<name>`) until linked, as with Meta; a phone number (`+447…`) is the account itself and global; the owner
  (`ownerName`) is one identity everywhere (`self:<name>`). Lines the app wrote are system messages from `whatsapp:system`. A chat
  is a group when more than two people wrote, or two wrote and the chat is named after neither; `conversationKind` overrides the
  guess. An iPhone export's bare `_chat.txt` names no chat, so the import asks for `conversationTitle`.
- **Taken time of a photo:** exact to the second when the camera recorded its UTC offset, else read on Klives's clock and marked
  `InferredZone`. `media_objects.taken_local` keeps the wall clock so the person-zone model (WS-B/C) can refine it.
- **HEIC/AVIF** are kept and marked `unsupported` (no decoder in this build; Magick.NET was not added).

## Shared-file edits (for the integrator)
- `Storage/Sqlite/SqliteExtensions.cs`: `PreparedStatement.Reader(...)` added (additive).
- `OmniscienceV3.BuildModules()`: now an instance method (it resolves KliveLLM for the transcriber) with four modules added
  (`ImportModule`, `MediaModule`, `MediaTextModule`, `DiscordBotModule`), plus `ReadKliveBotTokenAsync`.
  `Api/V3Routes.RegisterAsync`: one line (`IngestRoutes`).
- `Kernel/ModuleHost.DisposeAsync`: modules that implement `IAsyncDisposable` / `IDisposable` are disposed after the workers stop,
  in reverse start order (additive; no existing module implements either).
- `Api/ApiPlumbing.cs`: `RouteRegistrar.StreamingRouteAsync` (a route that reads its body as a stream; additive).
- `Storage/Legacy/DiscordRaw.cs`: `IsSystemType(int)` (additive). The legacy importer still sets `is_system` from the author's
  system flag only, so a join notice migrated from v2 has `is_system=0` and its `type` says what it is; live rows set both.
- `Omnipotent/Omnipotent.csproj`: `SkiaSharp` 3.119.0 (MIT) referenced explicitly; it was already resolved at that version
  through ScottPlot, so nothing new is downloaded.
- `Docs/omniscience-v3/api-contracts.ts` regenerated (import, upload and Discord status DTOs).

## Tests (`Omnipotent.Tests/OmniscienceV3/Ingest/`, synthetic data only)
`CorpusWriterTests` (12), `ImportTests` (16), `ImportInboxTests` (2), `MediaTests` (10), `MediaTextTests` (9), `DiscordIngestTests` (18: payload
reading, merges, events, the API client against a stub HTTP handler), `DiscordBotTests` (12: history paging, and the module end to
end against `FakeFeed` and `FakeDiscord` in `DiscordFixtures.cs`). Whole v3 suite on this branch: 239 passed, 1 skipped (the opt-in
5M perf run). Run: `$env:DOTNET_ROLL_FORWARD='Major'; dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj --filter "FullyQualifiedName~OmniscienceV3"`.

## Not built yet
| Piece | State |
|---|---|
| KliveBot live ingest and history (I-KliveBot) | **built, never run against Discord.** Tests use a stand-in connection and a stand-in bot API. `KliveBotFeed` (the DSharpPlus side) and `DiscordBotApi` against the real service compile and are covered only as far as a stub HTTP handler goes. First real run is at cutover or a deliberate trial with `ingest.discord_bot.enabled=true`. |
| Degrading to unprivileged intents on close code 4014 | not built. It needs a change to the KliveBot service's connect logic, which could not be exercised here and which every notification depends on. v3 reports the effect instead (`contentIntent=false` in `sources/discord`) and stores blanked text as unknown. |
| Refreshing expired attachment links (F403) | **built, tested with stand-ins.** A download answered 401/403/404/410 asks the link's source once for a fresh link (`IMediaLinkRefresher`): for Discord, `DiscordLinkRefresher` reads the message again through the bot API and takes the file with the same id, or at the same position. The fresh link must pass the same allow-list, is kept on the attachment, and is fetched at once; no fresh link, or a refused one, ends as `unavailable`; a failed ask is transient. Only while `ingest.discord_bot.enabled` is on and the token is accepted, so shadow mode sends Discord nothing. Never run against Discord. |
| Poll votes over time, pins, scheduled events, invites | not captured (`poll_votes` stays empty; polls themselves are stored). |
| Avatar files | hashes and their history are stored; the pictures are not downloaded. |
| Private archived threads | not listed (needs Manage Threads); public archived threads and forum posts are. |
| Upload of a folder or multipart form | not built: `imports/upload` takes one file as the raw body. |
| OCR and transcripts (N3) | **built, tested with stand-in engines only.** The real paths — Tesseract with language data from ModelHub, ffmpeg cutting, Whisper through KliveLLM's AIRouter audio client — compile but have never run: no test downloads language data or calls the speech service. First real run is part of the shadow run. |
| Captions (N2) → `labels` task `caption` | **built, tested with a scripted model** (`89f7830`, module `media.caption`, `Ingest/Media/MediaCaptionModule.cs`). The gateway takes images now (`LlmTurn.Images`, `LlmImage`; calls with images are logged in `llm_calls.images` and left out of the prefix-cache measure). The image is shown to the model as a JPEG of at most 768 px. Background work under a daily budget (`media.caption.daily_budget`, 300); tracked, watched and Klives's own images first; one caption per picture. The prompt never says who anyone is and describes graphic images in neutral words with a flag. Nothing is captioned for someone under 18 (or who may be) or with analysis off. Never run against the real model. |
| Prioritised backlog order (tracked people first, plan R2) for OCR/transcripts | **built, tested.** The `media.text` sweep first walks the backlog of the people Klives tracks and his own (job priority 185) and of those he watches (178), merged people counted as the person they joined, then everyone else in attachment order (170). One full pass per set of people and engines; media of theirs that arrives later is announced by the change log. |
| Visual search (N2): local image embeddings, search route, image pivot X24 | **built except the pivot X24** (`008e9c0`, module `media.visual`, `Ml/Vision/Clip.cs`, `GET media/search`). The model is OpenAI's CLIP ViT-B/32 (MIT), the quantised ONNX export of Xenova/clip-vit-base-patch32 at a pinned commit: image tower 89 MB, text tower 65 MB, each file pinned by SHA-256 and size; Klives agreed to the download. Every stored image gets a local embedding, with a sweep for the backlog. Search ranks images by how they look and by the words in their captions and OCR text (fused by reciprocal rank), says why each was found, and filters by poster, conversation and time. Images posted by someone under 18 (or who may be) or with analysis off are neither embedded nor returned. The opt-in real-model test was run once on this machine; the search has not run on a real corpus. |
| Faces (N1): detection, embedding, clustering, conversation-grounded labels, propagation, face pivot X25 | not started. The Phase 1 spike that was to pick the face stack (OpenCvSharp's detectors vs YuNet/SFace on ONNX Runtime) left no recorded result, so that choice is open too, and it also needs model files here. Guardrails as recorded: local only, never an external lookup, no labels for minors, meme and celebrity faces not treated as people Klives knows. |
| Explore datasets for media/imports (D4 manifest declarations) | not started |
| ffprobe for video/audio duration and dimensions | **built, tested with a stand-in.** `IAvProbe` / `FfprobeAvProbe` (FFMpegCore, the ffmpeg folder transcription already uses): the media probe fills an audio or video attachment's `duration_ms`, `width` and `height` where the platform said nothing, once per distinct content. A file ffprobe cannot read is `unsupported` (not quarantined: transcription may still read its sound); without ffprobe installed nothing is recorded, so a later probe can read it. Never run against a real ffprobe here. |
| Traceability | F359, F360, F394, F404–F406, F460–F471 fixed; F403 fixed but never run against Discord; F422 in progress; OCR rows (F334, F335, F407, F472–F476) and the Discord rows (see traceability.md) are marked built-not-run until the real engines and the real service have been exercised; user-gateway and vault rows are not applicable (no user-token gateway) |

## Known limits
- Discord events between a crash and the next start are not replayed: messages are recovered by the forward read, but
  reactions return only as counts, and typing, presence and voice changes in the gap are lost (voice sessions are closed as
  `unseen`).
- If events arrive faster than they are written, the newest are dropped once 20,000 are waiting (`eventsDropped` in the
  status); messages are still recovered by the forward read.
- A message deleted before it was ever read is never known. A deletion event for a message not in the corpus is ignored.
- Discord gives no time for a reaction: the time stored is when the event was heard.
- The bot's DMs are only found when they are in the library's cache or someone writes; closed DMs cannot be listed.
- A refused token stops Discord reading until the bot reconnects (the service or the bot restarts).
- WhatsApp: a group in which only two people ever wrote and which is named after one of them is read as a direct chat
  (override with `conversationKind`). A
  system line containing ": " in an Android export (rare) is read as a message from a sender named by the text before the colon.
  Two different contacts saved under the same name in one chat cannot be told apart from the export.
- WhatsApp exports from two different phones of the same chat (minute vs second precision) do not dedupe against each other.
- EXIF taken-time is read from JPEG only (not PNG `eXIf` or WebP).
- OCR and transcript jobs run on the Io lane (one job kind covers both; the kind of file is only known when the job runs),
  so a burst of OCR competes with downloads rather than with CPU-lane work.
- The media-text sweep treats an attachment as done once it has any `ocr`/`transcript` label, so changing the OCR languages
  re-reads new attachments but not the backlog.
- Tesseract language data is pinned on first download (trust on first use in the model lock file), not to a hash in the source.
- The import preview reads the whole export (a job, not a request); for a multi-gigabyte Telegram export that takes minutes.
