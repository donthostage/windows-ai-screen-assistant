BEGIN IMMEDIATE;

CREATE TABLE operator_audit (
    id INTEGER PRIMARY KEY,
    action TEXT NOT NULL CHECK (action IN ('license-issued', 'license-revoked', 'credit-granted', 'analysis-settled')),
    license_id TEXT NOT NULL REFERENCES licenses (id),
    reference_id TEXT,
    credits INTEGER CHECK (credits IS NULL OR credits >= 0),
    occurred_at INTEGER NOT NULL
) STRICT;

CREATE TRIGGER audit_license_issued AFTER INSERT ON licenses
BEGIN
    INSERT INTO operator_audit (action, license_id, occurred_at)
    VALUES ('license-issued', NEW.id, NEW.issued_at);
END;

CREATE TRIGGER audit_license_revoked AFTER UPDATE OF revoked_at ON licenses
WHEN OLD.revoked_at IS NULL AND NEW.revoked_at IS NOT NULL
BEGIN
    INSERT INTO operator_audit (action, license_id, occurred_at)
    VALUES ('license-revoked', NEW.id, NEW.revoked_at);
END;

CREATE TRIGGER audit_credit_granted AFTER INSERT ON credit_grants
BEGIN
    INSERT INTO operator_audit (action, license_id, reference_id, credits, occurred_at)
    VALUES ('credit-granted', NEW.license_id, NEW.grant_id, NEW.credits, NEW.granted_at);
END;

CREATE TRIGGER audit_analysis_settled AFTER UPDATE OF actual ON analysis_reservations
WHEN OLD.actual IS NULL AND NEW.operator_settled = 1 AND NEW.actual IS NOT NULL
BEGIN
    INSERT INTO operator_audit (action, license_id, reference_id, credits, occurred_at)
    VALUES ('analysis-settled', NEW.license_id, NEW.operation_id, NEW.actual, NEW.settled_at);
END;

PRAGMA user_version = 6;
COMMIT;
