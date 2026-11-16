BEGIN IMMEDIATE;
CREATE INDEX analysis_by_reservation_time ON analysis_reservations (reserved_at);
PRAGMA user_version = 5;
COMMIT;
