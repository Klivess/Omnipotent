namespace Omnipotent.Services.KliveMail.Persistence
{
    public static class KliveMailSchema
    {
        public static readonly (int Version, string Sql)[] Migrations = new (int, string)[]
        {
            (1, @"
                -- Addresses the user has explicitly created / pinned. Mail still arrives for ANY
                -- address@klive.dev (full catch-all); this table just drives the folder list / grouping.
                CREATE TABLE mailboxes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    address TEXT NOT NULL UNIQUE,        -- lowercased, e.g. shopping@klive.dev
                    display_name TEXT,
                    created_utc TEXT NOT NULL
                );

                -- One row per (envelope recipient @klive.dev) per received message.
                CREATE TABLE messages (
                    id TEXT PRIMARY KEY,                 -- our generated id (GUID 'N')
                    to_address TEXT NOT NULL,            -- lowercased envelope recipient @klive.dev
                    from_address TEXT NOT NULL,
                    from_name TEXT,
                    subject TEXT,
                    date_utc TEXT,                       -- header Date (ISO-8601 UTC), nullable
                    received_utc TEXT NOT NULL,          -- when KliveMail accepted it (ISO-8601 UTC)
                    message_id TEXT,                     -- RFC 5322 Message-Id
                    in_reply_to TEXT,
                    references_raw TEXT,
                    thread_id TEXT NOT NULL,
                    body_text TEXT,
                    body_html TEXT,
                    has_attachments INTEGER NOT NULL DEFAULT 0,
                    raw_size INTEGER NOT NULL DEFAULT 0,
                    is_read INTEGER NOT NULL DEFAULT 0,
                    is_deleted INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX idx_messages_to ON messages(to_address);
                CREATE INDEX idx_messages_thread ON messages(thread_id);
                CREATE INDEX idx_messages_received ON messages(received_utc);
                CREATE INDEX idx_messages_read ON messages(is_read);
                CREATE INDEX idx_messages_deleted ON messages(is_deleted);

                CREATE TABLE attachments (
                    id TEXT PRIMARY KEY,
                    message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                    file_name TEXT NOT NULL,
                    content_type TEXT,
                    size_bytes INTEGER NOT NULL DEFAULT 0,
                    storage_path TEXT NOT NULL,
                    is_inline INTEGER NOT NULL DEFAULT 0,
                    content_id TEXT
                );
                CREATE INDEX idx_attachments_message ON attachments(message_id);

                -- Standalone FTS5 index (populated/maintained explicitly by the repository, no triggers).
                CREATE VIRTUAL TABLE messages_fts USING fts5(
                    message_id UNINDEXED,
                    from_address,
                    from_name,
                    subject,
                    body_text
                );
            "),
            (2, @"
                -- Feature #1: extend the FTS index with decoded HTML body text and attachment search
                -- text so HTML-only messages and attachment content become searchable. FTS5 virtual
                -- tables cannot be ALTERed in place, so rebuild: rename the v1 table, create the
                -- extended v2 table, copy the existing rows (new columns stay empty), then drop v1.
                -- A one-off C# backfill pass (KliveMailDb) recomputes body_html/attachments for the
                -- rows carried over from v1, using the same HtmlToText/AttachmentSearchText logic the
                -- insert path uses, so old messages become searchable too.
                ALTER TABLE messages_fts RENAME TO messages_fts_v1;

                CREATE VIRTUAL TABLE messages_fts USING fts5(
                    message_id UNINDEXED,
                    from_address,
                    from_name,
                    subject,
                    body_text,
                    body_html,
                    attachments
                );

                INSERT INTO messages_fts(message_id, from_address, from_name, subject, body_text)
                    SELECT message_id, from_address, from_name, subject, body_text FROM messages_fts_v1;

                DROP TABLE messages_fts_v1;
            "),
            (3, @"
                -- List speed. Each row stores is_read / is_deleted / has_attachments after
                -- body_html, so reading any of them walks the row's overflow chain (~10 pages for a
                -- typical 40 KB marketing mail), and with no statistics SQLite picked the
                -- one-bit idx_messages_deleted for every query and sorted every row. The live list
                -- took 5-35 s whenever those pages were not cached. These indexes carry the flags
                -- the lists filter and show (and the id, which search matches against), in the
                -- order they are read, so a page of 50 touches 50 rows' first pages and nothing else.
                CREATE INDEX idx_messages_feed ON messages(is_deleted, received_utc, is_read, has_attachments, to_address, id);
                CREATE INDEX idx_messages_box ON messages(to_address, is_deleted, received_utc, is_read, has_attachments, id);
                CREATE INDEX idx_messages_conv ON messages(thread_id, is_deleted, received_utc, is_read, has_attachments, id);
                DROP INDEX IF EXISTS idx_messages_deleted;
                DROP INDEX IF EXISTS idx_messages_read;

                -- What a list row shows from the body, worked out once on arrival (KliveMailDigest):
                -- the preview (from the HTML when there is no text part) and any sign-in code.
                -- Derived from immutable content, so it never needs updating with read/delete state.
                CREATE TABLE message_digest (
                    message_id TEXT PRIMARY KEY,
                    snippet TEXT NOT NULL DEFAULT '',
                    code TEXT,
                    version INTEGER NOT NULL
                );
            ")
        };
    }
}
