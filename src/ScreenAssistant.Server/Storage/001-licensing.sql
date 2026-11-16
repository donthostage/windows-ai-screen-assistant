PRAGMA foreign_keys = ON;

BEGIN IMMEDIATE;

CREATE TABLE licenses (
    id TEXT NOT NULL PRIMARY KEY,
    activation_code_hash BLOB UNIQUE
        CHECK (activation_code_hash IS NULL OR length(activation_code_hash) = 32),
    device_public_key BLOB
        CHECK (device_public_key IS NULL OR length(device_public_key) BETWEEN 1 AND 128),
    issued_at INTEGER NOT NULL,
    activated_at INTEGER,
    revoked_at INTEGER,
    CHECK (
        (activated_at IS NULL AND device_public_key IS NULL)
        OR (activated_at IS NOT NULL AND device_public_key IS NOT NULL AND activation_code_hash IS NULL)
    ),
    CHECK (revoked_at IS NULL OR activation_code_hash IS NULL),
    CHECK (activated_at IS NULL OR activated_at >= issued_at),
    CHECK (revoked_at IS NULL OR revoked_at >= COALESCE(activated_at, issued_at))
) STRICT;

CREATE TABLE activation_challenges (
    id TEXT NOT NULL PRIMARY KEY,
    license_id TEXT NOT NULL REFERENCES licenses (id) ON DELETE CASCADE,
    nonce BLOB NOT NULL UNIQUE CHECK (length(nonce) = 32),
    device_public_key BLOB NOT NULL CHECK (length(device_public_key) BETWEEN 1 AND 128),
    issued_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL CHECK (expires_at > issued_at),
    consumed_at INTEGER CHECK (consumed_at IS NULL OR consumed_at >= issued_at AND consumed_at < expires_at)
) STRICT;

CREATE INDEX activation_challenges_license ON activation_challenges (license_id);
CREATE INDEX activation_challenges_expiry ON activation_challenges (expires_at);

PRAGMA user_version = 1;

COMMIT;
