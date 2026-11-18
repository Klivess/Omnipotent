# Projects is now Demiurge

November 2026. The autonomous task-force system formerly called **Projects** is now **Demiurge**.
One of its goal-plus-budget task forces, formerly a *project*, is now a **Steward**.

| Before | After |
|---|---|
| `Omnipotent/Services/Projects`, class `Projects` | `Omnipotent/Services/Demiurge`, class `Demiurge` |
| `ProjectStore`, `ProjectID`, `ProjectEvent`… | `StewardStore`, `StewardID`, `StewardEvent`… |
| API `/projects/...` | `/demiurge/...` |
| Website `/projects`, `/projects/<id>` | `/demiurge`, `/demiurge/<stewardId>` (old links 301) |
| Permission keys `projects.*` | `demiurge.*` |
| OmniSettings `Projects_*` | `Demiurge_*` |
| Environment `PROJECTS_*` | `DEMIURGE_*` (the old names are still read) |
| `SavedData/Projects` | `SavedData/Demiurge` |
| Docker owner label `projects`, image `omnipotent/projects-desktop` | `demiurge`, `omnipotent/demiurge-desktop` |
| Discord "Active Projects", `#project-<name>` | "Active Stewards", `#steward-<name>` |
| KliveLLM service `Projects` | `Demiurge` |

## Kept under the old name on purpose

- **`/project`**, the workspace path inside every desktop container. Agents' own scripts and stored paths use it.
- **`"projectvault:"`**, the HKDF info that derives each Steward's vault key. A new value would make every
  existing vault undecryptable.
- **`projects-prefix-v3`**, the prompt-cache telemetry version. The cache-health kill switch compares it exactly.
- **KliveRAG keys**: sources `projects-events` and `projects-digests`, cursors `projects:<id>`, document ids
  `projevt:` and `projdigest:`. They are written into an index that is gigabytes on the server; renaming them
  would rewrite or re-embed all of it. The names are only displayed through labels.
- **`projects_import`**, the KliveLLM ledger's marker for its one-time journal import. Renaming it would run
  the import again and count that history twice.
- **The desktop image build context** (`desktop.Dockerfile`, `desktop-entrypoint.sh`, `browser-*.py`,
  `klive-cdp`, `klive-fp-*`). Any byte change forces a full image rebuild, so their comments still say Projects.
- KliveAgent's `ListProjectClasses` / `FindProjectClass`: there, "project" means the C# source tree.

## What happens on the first start after deploying

All of it is automatic and idempotent; a second start changes nothing.

1. **Data folder.** Before any store opens, `DemiurgeStorageMigration` removes every Demiurge desktop
   container (they bind-mount the old folder), then renames `SavedData/Projects` to `SavedData/Demiurge`,
   along with `projects.json` → `stewards.json` and `FileMetadata/project-files.db` → `steward-files.db`.
   Desktops come back on first use. The desktop session restarts once; files, browser sign-ins and installed
   runtimes all live in the volume and carry over. If the folder cannot be moved (a file in it is open),
   Demiurge works from the old folder and tries again on the next start. Discord gets a note either way.
   The existing desktop image is tagged under the new name, not rebuilt.
2. **Stored records** keep their old JSON names on disk (`"ProjectID"`…) and load under the new ones
   (`DemiurgeLegacyNameResolver`). They are written in the new spelling the next time they are saved. Old event
   types (`project-blocked`…) read as their new spelling.
3. **Databases.** `steward-files.db` and `retrieval.sqlite` rename their `project_*` tables and columns in
   place; the retrieval full-text index is rebuilt from its kept rows (once, at startup). The KliveLLM ledger
   moves the `Projects` service history to `Demiurge`, and the routing document re-keys policies and budgets,
   so a budget does not reset part-way through a month.
4. **Access and settings.** Profile grants under `projects.*` move to `demiurge.*`. OmniSettings saved by
   Projects move to Demiurge with their values. Shared accounts owned by `project:<id>` read as
   `steward:<id>`.
5. **Discord.** The three status categories are renamed in place; each `#project-<name>` channel is renamed
   (with its topic) the next time Demiurge reconciles it.

Still accepted from before the rename: `/projects/hooks/webhook` with `X-Project-Hook-Token` (external
services keep working), `projectID` request parameters, KliveAgent's `CreateProject` / `ListProjects` /
`SteerProject` / `GetProjectStatus` script calls, and tool names such as `update_project`.

## Checks after deploying

- Discord: "Demiurge moved its data to …SavedData/Demiurge" (or the reason it is still on the old folder).
- `serverBuild/SavedData/Demiurge` exists and `SavedData/Projects` is gone.
- `/demiurge` lists every Steward; a Steward's desktop comes back when its agent next needs it.
- The KliveLLM page shows Demiurge's lanes with their earlier history and budgets.
- An old `https://klive.dev/projects/<id>` link lands on `/demiurge/<id>`.

The compatibility paths are covered by `Omnipotent.Tests/Demiurge/DemiurgeRenameCompatibilityTests.cs`.
