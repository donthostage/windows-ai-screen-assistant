BEGIN IMMEDIATE;

CREATE TABLE analysis_reservations (
    license_id TEXT NOT NULL REFERENCES licenses (id),
    operation_id TEXT NOT NULL,
    request_hash BLOB NOT NULL CHECK (length(request_hash) = 32),
    reserved INTEGER NOT NULL CHECK (reserved BETWEEN 1 AND 1000000000),
    actual INTEGER CHECK (actual BETWEEN 0 AND reserved),
    PRIMARY KEY (license_id, operation_id)
) STRICT;

CREATE INDEX analysis_unsettled ON analysis_reservations (license_id) WHERE actual IS NULL;
PRAGMA user_version = 3;

COMMIT;
