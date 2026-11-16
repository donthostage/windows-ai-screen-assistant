using Microsoft.Data.Sqlite;
using ScreenAssistant.Core.Licensing;

namespace ScreenAssistant.Server;

internal sealed partial class LicenseStore
{
    private const int ReconciliationDelaySeconds = 120;

    internal IReadOnlyList<PendingReservation> ListPendingAnalysis()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT license_id, operation_id, reserved, reserved_at
            FROM analysis_reservations
            WHERE actual IS NULL AND reserved_at <= $cutoff
            ORDER BY reserved_at, license_id, operation_id
            LIMIT 100
            """;
        command.Parameters.AddWithValue("$cutoff", Now() - ReconciliationDelaySeconds);
        using var rows = command.ExecuteReader();
        var result = new List<PendingReservation>();
        while (rows.Read())
        {
            var timestamp = rows.GetInt64(3);
            result.Add(new(Guid.ParseExact(rows.GetString(0), "N"), Guid.ParseExact(rows.GetString(1), "N"),
                rows.GetInt64(2), timestamp == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(timestamp)));
        }
        return result;
    }

    internal bool VerifyAnalysis(Guid licenseId, Guid operationId, long timestamp, byte[] requestHash, byte[] signature)
    {
        var now = Now();
        if (timestamp < now - 60 || timestamp > now + 60 || signature.Length != 64)
            return false;

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT device_public_key FROM licenses
            WHERE id = $license AND activated_at IS NOT NULL AND revoked_at IS NULL
            """;
        command.Parameters.AddWithValue("$license", licenseId.ToString("N"));
        return command.ExecuteScalar() is byte[] key
            && DeviceActivationProof.VerifyAnalysis(key, licenseId, operationId, timestamp, requestHash, signature);
    }

    internal ReservationResult ReserveAnalysis(Guid licenseId, Guid operationId, byte[] requestHash, long credits, long dailyLimit = MaximumCredits)
    {
        if (operationId == Guid.Empty || requestHash.Length != 32 || credits is < 1 or > MaximumCredits
            || dailyLimit is < 1 or > MaximumCredits)
            throw new ArgumentException("Invalid analysis reservation.");

        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = AnalysisCommand(db, tx, licenseId, operationId);
        command.CommandText = """
            SELECT request_hash FROM analysis_reservations
            WHERE license_id = $license AND operation_id = $operation
            """;
        if (command.ExecuteScalar() is byte[] previous)
            return previous.AsSpan().SequenceEqual(requestHash) ? ReservationResult.Duplicate : ReservationResult.Conflict;

        command.CommandText = "SELECT available_credits FROM licenses WHERE id = $license AND activated_at IS NOT NULL AND revoked_at IS NULL";
        if (command.ExecuteScalar() is not long available)
            return ReservationResult.Inactive;
        if (available < credits)
            return ReservationResult.Insufficient;

        var now = Now();
        command.CommandText = """
            SELECT coalesce(sum(coalesce(actual, reserved)), 0)
            FROM analysis_reservations WHERE reserved_at >= $startOfDay
            """;
        command.Parameters.AddWithValue("$startOfDay", now - now % 86_400);
        var usedToday = (long)command.ExecuteScalar()!;
        if (usedToday > dailyLimit - credits)
            return ReservationResult.DailyLimit;

        command.CommandText = """
            UPDATE licenses SET available_credits = available_credits - $credits WHERE id = $license;
            INSERT INTO analysis_reservations (license_id, operation_id, request_hash, reserved, reserved_at)
            VALUES ($license, $operation, $hash, $credits, $now);
            """;
        command.Parameters.AddWithValue("$credits", credits);
        command.Parameters.AddWithValue("$hash", requestHash);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        tx.Commit();
        return ReservationResult.Reserved;
    }

    internal bool SettleAnalysis(Guid licenseId, Guid operationId, long actualCredits, bool byOperator = false)
    {
        if (actualCredits is < 0 or > MaximumCredits)
            throw new ArgumentOutOfRangeException(nameof(actualCredits));

        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = AnalysisCommand(db, tx, licenseId, operationId);
        command.CommandText = """
            UPDATE analysis_reservations
            SET actual = $actual, settled_at = $now, operator_settled = $operator
            WHERE license_id = $license AND operation_id = $operation
                AND actual IS NULL AND reserved >= $actual
                AND ($operator = 0 OR reserved_at <= $cutoff)
            RETURNING reserved - actual
            """;
        var now = Now();
        command.Parameters.AddWithValue("$actual", actualCredits);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$operator", byOperator ? 1 : 0);
        command.Parameters.AddWithValue("$cutoff", now - ReconciliationDelaySeconds);
        if (command.ExecuteScalar() is not long refund)
            return false;

        command.CommandText = """
            UPDATE licenses SET available_credits = available_credits + $refund WHERE id = $license
            """;
        command.Parameters.AddWithValue("$refund", refund);
        command.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    private static SqliteCommand AnalysisCommand(SqliteConnection db, SqliteTransaction tx,
        Guid licenseId, Guid operationId)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$license", licenseId.ToString("N"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("N"));
        return command;
    }
}

internal enum ReservationResult { Reserved, Duplicate, Conflict, Inactive, Insufficient, DailyLimit }

internal sealed record PendingReservation(Guid LicenseId, Guid OperationId, long ReservedCredits, DateTimeOffset? ReservedAt);
