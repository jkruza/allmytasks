-- One row per reminder that was delivered, snoozed or dismissed. Times are UTC milliseconds. The reminder is kept
-- whole so a snoozed one can be shown again without re-reading the item.
CREATE TABLE reminder_log (
    source_id      TEXT NOT NULL,
    collection_id  TEXT NOT NULL,
    item_id        TEXT NOT NULL,
    recurrence_id  TEXT NOT NULL DEFAULT '',
    occurrence_utc INTEGER NOT NULL,
    kind           INTEGER NOT NULL,
    title          TEXT NOT NULL,
    detail         TEXT NULL,
    fire_at_utc    INTEGER NOT NULL,
    ends_at_utc    INTEGER NOT NULL,
    state          INTEGER NOT NULL,
    snoozed_until_utc INTEGER NULL,
    PRIMARY KEY (source_id, collection_id, item_id, recurrence_id, occurrence_utc)
);

CREATE INDEX reminder_log_ends_at ON reminder_log (ends_at_utc);
