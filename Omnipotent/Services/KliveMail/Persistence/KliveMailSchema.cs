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
            ")
        };
    }
}
