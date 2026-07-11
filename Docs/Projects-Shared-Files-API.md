# Projects shared-files API

The Projects backend owns a persistent filesystem per project. Every Commander and worker sees
the same bytes at `/project`; website clients use the Klives-only HTTP routes below. JSON fields
are camel-cased. Paths are slash-separated and relative to the project root.

## Upload workflow

1. `POST /projects/files/uploads/start`

   ```json
   { "purpose": "initial", "projectID": null }
   ```

   `purpose` is `initial` or `existingProject`; an existing-project session requires `projectID`.
   The response includes `session.sessionID`, `session.expiresUtc`, `maxFileBytes`, and
   `maxChunkBytes`. Restore an interrupted browser session with
   `GET /projects/files/uploads/get?sessionID=...`, which also returns each file's confirmed
   `receivedSize`.

2. Upload each file sequentially by offset, while uploading up to three different files in
   parallel:

   `PUT /projects/files/uploads/chunk?sessionID=...&path=brand/logo.png&offset=0&expectedSize=1234&contentType=image/png`

   The request body is the raw chunk. The response returns `receivedSize`, `expectedSize`, and
   `complete`. Retry a failed request at the last confirmed `receivedSize`.

3. For a new project, pass `initialUploadSessionID` to `POST /projects/create`. The files are
   committed under `inputs/` before the first Commander wake. For an existing project call:

   ```http
   POST /projects/files/uploads/commit
   Content-Type: application/json

   {
     "sessionID": "...",
     "conflictPolicy": "Fail",
     "pathPolicies": { "shared/brand/logo.png": "Replace" }
   }
   ```

   Conflict policies are `Fail`, `Replace`, `KeepBoth`, and `Skip`. The server never silently
   overwrites. Cancel a session with `POST /projects/files/uploads/cancel` and `{ "sessionID": "..." }`.

Initial sessions expire after 24 hours. There is no file-count or per-project quota; production
defaults are 10 GiB per file, 8 MiB per chunk, and a 10 GiB free-disk reserve.
Those three limits are configurable with `Projects_FileMaxSizeGb`,
`Projects_FileUploadChunkMb`, and `Projects_FileFreeReserveGb`.

## Browsing and management

- `GET /projects/files/list?projectID=...&path=&recursive=false&query=&glob=&limit=100&cursor=`
- `GET /projects/files/stat?projectID=...&path=shared/brand/logo.png`
- `GET /projects/files/download?projectID=...&path=shared/brand/logo.png`
- `GET /projects/files/audit?projectID=...&limit=200&cursor=` — paged immutable
  operation/provenance history; follow `nextCursor` until it is null
- `POST /projects/files/directory` — `{ projectID, path }`
- `POST /projects/files/move` — `{ projectID, path, destination }`
- `POST /projects/files/copy` — `{ projectID, path, destination }`
- `POST /projects/files/delete` — `{ projectID, path, recursive: false }`
- `POST /projects/files/metadata` — `{ projectID, path, important, description }`

List/stat entries include stable `fileID`, kind, size, MIME type, SHA-256 when known, created and
modified timestamps, creator/modifier actors, origin, description, and important status. A direct
CLI/GUI change is reconciled with actor `Unknown`; the backend never guesses attribution.

Completed and archived projects are browse/download-only. File mutations publish one
`project-file-changed` event through the existing project event stream. A committed upload batch
publishes and wakes/steers the Commander once for the whole batch rather than once per file.

## Frontend expectations

- The new-project page stages file/folder selections before submitting project creation.
- Preserve browser-relative paths for folder uploads.
- Use three-file concurrency, per-file progress, offset resume, retry, cancellation, and a clear
  incomplete-upload state; do not enable Create while any selected file is incomplete.
- The project Files tab should show breadcrumbs, search, pagination, provenance, descriptions,
  important markers, upload/file-management actions, and an explicit conflict dialog.
- Treat downloads as attachments. Do not render uploaded HTML or SVG as trusted same-origin UI.
