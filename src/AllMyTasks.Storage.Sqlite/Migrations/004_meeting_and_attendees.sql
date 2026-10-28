-- Events cached before this version have no meeting link or attendees, and an incremental sync would not bring them back.
-- Clearing the sync state of calendar collections (kind 0) makes the next sync a full one.
UPDATE collections SET sync_token = NULL WHERE kind = 0;
