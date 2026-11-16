BEGIN IMMEDIATE;

ALTER TABLE analysis_reservations ADD COLUMN reserved_at INTEGER NOT NULL DEFAULT 0;
ALTER TABLE analysis_reservations ADD COLUMN settled_at INTEGER;
ALTER TABLE analysis_reservations ADD COLUMN operator_settled INTEGER NOT NULL DEFAULT 0
    CHECK (operator_settled IN (0, 1));

CREATE INDEX analysis_reconciliation_queue ON analysis_reservations (reserved_at, license_id, operation_id)
    WHERE actual IS NULL;

PRAGMA user_version = 4;

COMMIT;
