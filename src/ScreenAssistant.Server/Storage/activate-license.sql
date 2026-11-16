-- Execute inside BEGIN IMMEDIATE after verifying the signature against the
-- immutable nonce and public key for this challenge. Require one returned row.
UPDATE licenses
SET activated_at = :now,
    activation_code_hash = NULL,
    device_public_key = (
        SELECT device_public_key FROM activation_challenges WHERE id = :challenge_id
    )
WHERE revoked_at IS NULL
  AND activated_at IS NULL
  AND activation_code_hash IS NOT NULL
  AND id = (
      SELECT license_id
      FROM activation_challenges
      WHERE id = :challenge_id
        AND nonce = :verified_nonce
        AND device_public_key = :verified_public_key
        AND consumed_at IS NULL
        AND issued_at <= :now
        AND expires_at > :now
  )
RETURNING id;
