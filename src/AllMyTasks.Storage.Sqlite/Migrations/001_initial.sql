CREATE TABLE sources (
    id             TEXT NOT NULL PRIMARY KEY,
    kind           TEXT NOT NULL,
    display_name   TEXT NOT NULL,
    config_json    TEXT NOT NULL DEFAULT '{}',
    status         TEXT NULL,
    last_synced_at TEXT NULL,
    last_error     TEXT NULL
);

CREATE TABLE collections (
    source_id      TEXT NOT NULL REFERENCES sources (id) ON DELETE CASCADE,
    id             TEXT NOT NULL,
    display_name   TEXT NOT NULL,
    kind           INTEGER NOT NULL,
    color          TEXT NULL,
    color_override TEXT NULL,
    is_visible     INTEGER NOT NULL DEFAULT 1,
    is_read_only   INTEGER NOT NULL DEFAULT 0,
    sync_token     TEXT NULL,
    PRIMARY KEY (source_id, id)
);

-- Times are UTC milliseconds. All-day events store midnight UTC of their dates, so range queries can use one
-- pair of columns; the queries add a one-day margin and Core does the exact filtering.
CREATE TABLE events (
    source_id      TEXT NOT NULL,
    collection_id  TEXT NOT NULL,
    item_id        TEXT NOT NULL,
    recurrence_id  TEXT NOT NULL DEFAULT '',
    title          TEXT NOT NULL,
    all_day        INTEGER NOT NULL,
    start_utc      INTEGER NOT NULL,
    end_utc        INTEGER NOT NULL,
    start_date     TEXT NULL,
    end_date       TEXT NULL,
    is_floating    INTEGER NOT NULL DEFAULT 0,
    tz_id          TEXT NULL,
    status         INTEGER NOT NULL,
    is_recurring   INTEGER NOT NULL DEFAULT 0,
    until_utc      INTEGER NULL,
    last_modified  TEXT NULL,
    json           TEXT NOT NULL,
    PRIMARY KEY (source_id, collection_id, item_id, recurrence_id)
);

CREATE INDEX ix_events_range ON events (start_utc, end_utc);
CREATE INDEX ix_events_series ON events (source_id, collection_id, item_id);

CREATE TABLE tasks (
    source_id      TEXT NOT NULL,
    collection_id  TEXT NOT NULL,
    item_id        TEXT NOT NULL,
    title          TEXT NOT NULL,
    status         INTEGER NOT NULL,
    due_date       TEXT NULL,
    due_utc        INTEGER NULL,
    parent_item_id TEXT NULL,
    priority       INTEGER NOT NULL,
    last_modified  TEXT NULL,
    json           TEXT NOT NULL,
    PRIMARY KEY (source_id, collection_id, item_id)
);

CREATE INDEX ix_tasks_due ON tasks (due_utc);

CREATE TABLE settings (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);
