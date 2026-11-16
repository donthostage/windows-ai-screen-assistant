BEGIN IMMEDIATE;

ALTER TABLE licenses ADD COLUMN available_credits INTEGER NOT NULL DEFAULT 0
    CHECK (available_credits BETWEEN 0 AND 1000000000);

CREATE TABLE credit_grants (
    license_id TEXT NOT NULL REFERENCES licenses (id),
    grant_id TEXT NOT NULL,
    credits INTEGER NOT NULL CHECK (credits BETWEEN 1 AND 1000000000),
    granted_at INTEGER NOT NULL,
    PRIMARY KEY (license_id, grant_id)
) STRICT;

PRAGMA user_version = 2;
COMMIT;
