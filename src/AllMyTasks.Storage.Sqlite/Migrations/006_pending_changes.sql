-- IF NOT EXISTS because a test rewinds schema_info to re-run the later migrations on a finished database.
-- Changes the user made (complete or reopen a task) that have not been sent to the source yet.
-- kind: 0 complete, 1 reopen. state: 0 queued, 1 failed. created_at is UTC milliseconds.
CREATE TABLE IF NOT EXISTS pending_changes (
    id            TEXT NOT NULL PRIMARY KEY,
    source_id     TEXT NOT NULL REFERENCES sources (id) ON DELETE CASCADE,
    collection_id TEXT NOT NULL,
    item_id       TEXT NOT NULL,
    kind          INTEGER NOT NULL,
    created_at    INTEGER NOT NULL,
    attempts      INTEGER NOT NULL DEFAULT 0,
    state         INTEGER NOT NULL DEFAULT 0,
    error         TEXT NULL
);

CREATE INDEX IF NOT EXISTS pending_changes_source ON pending_changes (source_id, created_at);
