# Demiurge shared-files API

The Demiurge backend owns a persistent filesystem per Steward. Every Commander and worker sees
the same bytes at `/project`; website clients use the Klives-only HTTP routes below. JSON fields
are camel-cased. Paths are slash-separated and relative to the project root.

## Upload workflow

1. `POST /demiurge/files/uploads/start`

   ```json
   { "purpose": "initial", "stewardID": null }
   ```

   `purpose` is `initial` or `existingSteward`; an existing-Steward session requires `stewardID`.
   The response includes `session.sessionID`, `session.expiresUtc`, `maxFileBytes`, and
   `maxChunkBytes`. Restore an interrupted browser session with
   `GET /demiurge/files/uploads/get?sessionID=...`, which also returns each file's confirmed
   `receivedSize`.

2. Upload each file sequentially by offset, while uploading up to three different files in
   parallel:

   `PUT /demiurge/files/uploads/chunk?sessionID=...&path=brand/logo.png&offset=0&expectedSize=1234&contentType=image/png`

   The request body is the raw chunk. The response returns `receivedSize`, `expectedSize`, and
   `complete`. Retry a failed request at the last confirmed `receivedSize`.

3. For a new Steward, pass `initialUploadSessionID` to `POST /demiurge/create`. The files are
   committed under `inputs/` before the first Commander wake. For an existing Steward call:

   ```http
   POST /demiurge/files/uploads/commit
   Content-Type: application/json

   {
     "sessionID": "...",
     "conflictPolicy": "Fail",
     "pathPolicies": { "shared/brand/logo.png": "Replace" }
   }
   ```

   Conflict policies are `Fail`, `Replace`, `KeepBoth`, and `Skip`. The server never silently
   overwrites. Cancel a session with `POST /demiurge/files/uploads/cancel` and `{ "sessionID": "..." }`.

Initial sessions expire after 24 hours. There is no file-count or per-Steward quota; production
defaults are 10 GiB per file, 8 MiB per chunk, and a 10 GiB free-disk reserve.
Those three limits are configurable with `Demiurge_FileMaxSizeGb`,
`Demiurge_FileUploadChunkMb`, and `Demiurge_FileFreeReserveGb`.

## Browsing and management

- `GET /demiurge/files/list?stewardID=...&path=&recursive=false&query=&glob=&limit=100&cursor=`
- `GET /demiurge/files/stat?stewardID=...&path=shared/brand/logo.png`
- `GET /demiurge/files/download?stewardID=...&path=shared/brand/logo.png`
- `GET /demiurge/files/audit?stewardID=...&limit=200&cursor=` — paged immutable
  operation/provenance history; follow `nextCursor` until it is null
- `POST /demiurge/files/directory` — `{ stewardID, path }`
- `POST /demiurge/files/move` — `{ stewardID, path, destination }`
- `POST /demiurge/files/copy` — `{ stewardID, path, destination }`
- `POST /demiurge/files/delete` — `{ stewardID, path, recursive: false }`
- `POST /demiurge/files/metadata` — `{ stewardID, path, important, description }`

List/stat entries include stable `fileID`, kind, size, MIME type, SHA-256 when known, created and
modified timestamps, creator/modifier actors, origin, description, and important status. A direct
CLI/GUI change is reconciled with actor `Unknown`; the backend never guesses attribution.

Completed and archived Stewards are browse/download-only. File mutations publish one
`steward-file-changed` event through the existing Steward event stream. A committed upload batch
publishes and wakes/steers the Commander once for the whole batch rather than once per file.

## Frontend expectations

- The new-Steward page stages file/folder selections before submitting Steward creation.
- Preserve browser-relative paths for folder uploads.
- Use three-file concurrency, per-file progress, offset resume, retry, cancellation, and a clear
  incomplete-upload state; do not enable Create while any selected file is incomplete.
- The Steward Files tab should show breadcrumbs, search, pagination, provenance, descriptions,
  important markers, upload/file-management actions, and an explicit conflict dialog.
- Treat downloads as attachments. Do not render uploaded HTML or SVG as trusted same-origin UI.
