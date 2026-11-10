# WS-A — Ingest & media (progress)

Branch `claude/omniscience-v3-ws-a-878bce` (worktree `.claude/worktrees/omniscience-v3-ws-a-878bce`), cut from Phase 1 (3dc87df).
Charter: [workstreams.md](workstreams.md). The workstream's original session produced no commits; since 2026-11-10 the
coordinating session does this work itself, in this worktree. This file is kept current so any session can resume.

## Layout (`Omnipotent/Services/OmniscienceV3/Ingest/`)
| Path | What |
|---|---|
| `IngestModel.cs` | The normalised records every source produces: `IngestMessage`, `IngestIdentity`, `IngestConversation`, `IngestAttachment`, `IngestReaction`, `IngestSource`, `IngestStats`. |
| `CorpusWriter.cs` | **The one write path into corpus.db.** Natural-key upserts, versions instead of overwrites, source-ranked merges, name validity intervals from message time, one change-log row per mutation. |
| `Media/MediaStore.cs` | Content-addressed store (`media/ab/cd/<sha256>`), streamed and hashed, type sniffed from the bytes, `DownloadStates`. |
| `Media/ImageProbe.cs` | One read of an image: upright size, frames, EXIF taken-time (never GPS), perceptual hash, WebP thumbnail; `LoadFrames` for vision steps. |
| `Media/MediaProcessor.cs` | Downloading (allow-listed https hosts, no redirects, size ceilings) and probing (once per distinct content, quarantine of damaged files). |
| `Media/MediaModule.cs` | Module `media.store`: download and probe jobs from the change log, a sweep for media that predates the module. Intel table `media_objects`. |
| `Media/MediaText.cs` | Text from media: `MediaTextProcessor` (once per content, cached, labels per attachment), `TesseractOcr`, `FfmpegSegmenter`, `AiRouterTranscriber`, `TranscriptMerge`. |
| `Media/MediaTextModule.cs` | Module `media.text`: an `extract` job per stored attachment, a sweep for what was missed. Intel table `media_text`. |
| `Imports/ImportContracts.cs` | `ImportInput` (file, zip or folder), `ImportOptions`, `IImportFormat`, `TextDecoding` (BOMs, UTF-16, mojibake repair), `JsonPullReader` (streaming JSON), `ImportIds`. |
| `Imports/*Import.cs` | Formats: `WhatsAppImport`, `TelegramImport`, `MetaImport` (Instagram, Messenger), `DiscordPackageImport`, `TableImport` (CSV/TSV/JSON). |
| `Imports/ImportService.cs` | Detect → preview (no writes) → commit (idempotent, batched, media into the store). |
| `Imports/ImportModule.cs` | Module `ingest.imports`: preview and commit as kernel jobs on exports in the inbox folder. |
| `../Api/IngestRoutes.cs` | Routes below. |

## Modules
| Module | Jobs | Notes |
|---|---|---|
| `ingest.imports` | `preview`, `commit` | Lane Io. Single-flight per export; `MaxAttempts = 1` (a failed import is retried by Klives after he fixes what it named). |
| `media.store` | `download`, `probe`, `sweep` | Subscribes to `attachment.add` and `attachment.stored`. Sweep every 10 min. |
| `media.text` | `extract`, `sweep` | Depends on `media.store`; subscribes to `attachment.stored`. Sweep every 15 min. Lane Io. |

## Routes (all under `/omniscience/v3/`)
| Route | Permission | What |
|---|---|---|
| `GET imports` | `sources.view` | `ImportListDto`: the inbox folder's entries, the ledger, the known formats. |
| `POST imports/preview` | `sources.manage` | Body `ImportJobPayload {name, format?, options?}` → `ImportJobStartedDto {jobId}`. The job's `resultJson` is an `ImportPreview`. |
| `POST imports/commit` | `sources.manage` | Same body → job whose `resultJson` is an `ImportResult`. |
| `GET media/file?attachment=<id>` | `messages.read` | The attachment's stored bytes. 404 `not_stored` when there are none. |
| `GET media/thumb?attachment=<id>` | `messages.read` | Its WebP thumbnail (max side 320). 404 `not_built` when not an image or not yet probed. |

## What other workstreams read from here
- **Media text (WS-B's evidence reader):** `labels` rows with `item_kind='attachment'`, `item_id` = attachment id, `task` = `ocr` or
  `transcript`, `signals_json` = `{ text, language, origin, sha256 }` where `origin` is `image_text` or `speech` (quoted material,
  never the poster's own words). An empty `text` with `abstained=1` means "read, nothing there". `model_version` changes when
  the engine or its language data does; older labels stay beside the new ones. Task `caption` is not written yet.
- **Media facts:** intel `media_objects` (by content hash): upright size, frames, orientation, `phash`, `taken_local`,
  `thumb_sha256`, `state` (`ok` / `corrupt` / `unsupported`).
- **Frames for vision:** `ImageProbe.LoadFrames(path, maxFrames)` gives upright bitmaps (three spread frames of an animation).

## Answers to WS-E's questions (ws-e.md contract gaps)
- **How files reach the importers:** today only by the **inbox folder** (`SavedData/Omniscience/v3/imports/`): Klives drops a file,
  zip or unpacked folder there and names it in `imports/preview` / `imports/commit`. Jobs take a *name inside the inbox*, never a
  path, so the API cannot be pointed at arbitrary files. **There is no upload route yet** (see "Not built").
- **How media is served:** `media/file` and `media/thumb` by attachment id, above.
- **Face boxes:** not built yet. When they are, boxes will be fractions (0–1) of the **upright** image, the same image
  `media/file` shows once the viewer applies no rotation of its own (thumbnails are already upright).

## Decisions (recorded so they can be revisited)
- **Discord: the official bot gateway only.** The plan lists a user-token gateway (I-DiscordUser). It is **not built** in v3 and
  this workstream will not build it: the guardrail recorded with the approval is that v3 "uses only the official Discord bot
  gateway and adds nothing to disguise automation from Discord", and automating a user account is against Discord's terms.
  Klives's own history comes in through Discord's data package (the `discord-package` importer) and, until cutover, through the
  v2 tail. KliveBot (the bot gateway) is the live source to build. **This narrows the plan; it is Klives's call to reopen.**
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
- `OmniscienceV3.BuildModules()`: now an instance method (it resolves KliveLLM for the transcriber) with three modules added
  (`ImportModule`, `MediaModule`, `MediaTextModule`). `Api/V3Routes.RegisterAsync`: one line (`IngestRoutes`).
- `Omnipotent/Omnipotent.csproj`: `SkiaSharp` 3.119.0 (MIT) referenced explicitly; it was already resolved at that version
  through ScottPlot, so nothing new is downloaded.
- `Docs/omniscience-v3/api-contracts.ts` regenerated (import DTOs).

## Tests (`Omnipotent.Tests/OmniscienceV3/Ingest/`, synthetic data only)
`CorpusWriterTests` (12), `ImportTests` (16), `MediaTests` (10), `MediaTextTests` (9). Whole v3 suite on this branch: 208 passed, 1 skipped (the opt-in
5M perf run). Run: `$env:DOTNET_ROLL_FORWARD='Major'; dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj --filter "FullyQualifiedName~OmniscienceV3"`.

## Not built yet
| Piece | State |
|---|---|
| KliveBot live ingest (bot gateway, I-KliveBot) | not started |
| Discord history backfill through the bot (channels the bot is in) | not started |
| Upload route for exports (PUT stream into the inbox) | not started; the inbox folder works today |
| Drop-folder auto-preview | not started (previews are requested explicitly) |
| OCR and transcripts (N3) | **built, tested with stand-in engines only.** The real paths — Tesseract with language data from ModelHub, ffmpeg cutting, Whisper through KliveLLM's AIRouter audio client — compile but have never run: no test downloads language data or calls the speech service. First real run is part of the shadow run. |
| Captions (N2) → `labels` task `caption` | not started |
| Prioritised backlog order (tracked people first, plan R2) for OCR/transcripts | not built: the sweep goes in attachment-id order |
| Visual search (N2): local image embeddings, search route, image pivot X24 | not started |
| Faces (N1): detection, embedding, clustering, conversation-grounded labels, propagation, face pivot X25 | not started |
| Explore datasets for media/imports (D4 manifest declarations) | not started |
| ffprobe for video/audio duration and dimensions | not started |
| Traceability | F359, F360, F394, F404–F406, F460–F471 fixed; F403, F422 in progress; OCR rows (F334, F335, F407, F472–F476) built but left unticked until the real engines have run; gateway and vault rows untouched |

## Known limits
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
