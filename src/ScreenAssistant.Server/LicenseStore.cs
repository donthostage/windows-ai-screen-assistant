using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ScreenAssistant.Core.Licensing;

namespace ScreenAssistant.Server;

internal sealed partial class LicenseStore(string databasePath, TimeProvider clock)
{
    private static readonly string LicensingMigration = ReadSql("001-licensing.sql");
    private static readonly string CreditsMigration = ReadSql("002-credits.sql");
    private static readonly string ReservationsMigration = ReadSql("003-analysis-reservations.sql");
    private static readonly string ReconciliationMigration = ReadSql("004-reconciliation.sql");
    private static readonly string BudgetMigration = ReadSql("005-daily-budget.sql");
    private static readonly string AuditMigration = ReadSql("006-operator-audit.sql");
    private static readonly string Activate = ReadSql("activate-license.sql");

    public void Initialize()
    {
        using var db = Open();
        using var schema = db.CreateCommand();
        schema.CommandText = "PRAGMA user_version";
        switch (Convert.ToInt64(schema.ExecuteScalar()))
        {
            case 6: return;
            case 0:
                schema.CommandText = LicensingMigration;
                schema.ExecuteNonQuery();
                goto case 1;
            case 1:
                schema.CommandText = CreditsMigration;
                schema.ExecuteNonQuery();
                goto case 2;
            case 2:
                schema.CommandText = ReservationsMigration;
                schema.ExecuteNonQuery();
                goto case 3;
            case 3:
                schema.CommandText = ReconciliationMigration;
                schema.ExecuteNonQuery();
                goto case 4;
            case 4:
                schema.CommandText = BudgetMigration;
                schema.ExecuteNonQuery();
                goto case 5;
            case 5:
                schema.CommandText = AuditMigration;
                schema.ExecuteNonQuery();
                return;
            default: throw new InvalidOperationException("Unsupported license database schema.");
        }
    }

    public IssuedLicense Issue()
    {
        var id = Guid.NewGuid().ToString("N");
        var code = RandomNumberGenerator.GetBytes(32);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO licenses (id, activation_code_hash, issued_at)
            VALUES ($id, $hash, $now)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$hash", SHA256.HashData(code));
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.ExecuteNonQuery();
        return new(id, Convert.ToHexString(code));
    }

    public ActivationChallenge? Start(string? code, byte[]? publicKey)
    {
        if (code is not { Length: 64 } || publicKey is not { Length: > 0 and <= 128 })
            return null;

        byte[] secret;
        try { secret = Convert.FromHexString(code); }
        catch (FormatException) { return null; }

        var id = Guid.NewGuid().ToString("N");
        var nonce = DeviceActivationProof.CreateChallenge();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var now = Now();
        using var cleanup = db.CreateCommand();
        cleanup.Transaction = tx;
        cleanup.CommandText = "DELETE FROM activation_challenges WHERE expires_at <= $now";
        cleanup.Parameters.AddWithValue("$now", now);
        cleanup.ExecuteNonQuery();

        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO activation_challenges
                (id, license_id, nonce, device_public_key, issued_at, expires_at)
            SELECT $id, id, $nonce, $key, $now, $expires
            FROM licenses
            WHERE activation_code_hash = $hash AND revoked_at IS NULL
              AND activated_at IS NULL
              AND (SELECT count(*) FROM activation_challenges WHERE license_id = licenses.id) < 8
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$nonce", nonce);
        cmd.Parameters.AddWithValue("$key", publicKey);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$expires", now + 120);
        cmd.Parameters.AddWithValue("$hash", SHA256.HashData(secret));
        var inserted = cmd.ExecuteNonQuery();
        tx.Commit();
        return inserted == 1 ? new(id, nonce, DateTimeOffset.FromUnixTimeSeconds(now + 120)) : null;
    }

    public string? Complete(string? challengeId, byte[]? signature)
    {
        if (!Guid.TryParseExact(challengeId, "N", out _) || signature is not { Length: 64 })
            return null;

        using var db = Open();
        using var read = db.CreateCommand();
        read.CommandText = """
            SELECT nonce, device_public_key FROM activation_challenges
            WHERE id = $id AND consumed_at IS NULL AND expires_at > $now
            """;
        read.Parameters.AddWithValue("$id", challengeId!);
        read.Parameters.AddWithValue("$now", Now());
        byte[] nonce;
        byte[] key;
        using (var row = read.ExecuteReader())
        {
            if (!row.Read())
                return null;

            nonce = (byte[])row[0];
            key = (byte[])row[1];
        }

        if (!DeviceActivationProof.Verify(key, nonce, signature))
            return null;

        using var tx = db.BeginTransaction(deferred: false);
        var now = Now();
        using var activate = db.CreateCommand();
        activate.Transaction = tx;
        activate.CommandText = Activate;
        activate.Parameters.AddWithValue(":challenge_id", challengeId!);
        activate.Parameters.AddWithValue(":verified_nonce", nonce);
        activate.Parameters.AddWithValue(":verified_public_key", key);
        activate.Parameters.AddWithValue(":now", now);
        if (activate.ExecuteScalar() is not string licenseId)
            return null;

        using var consume = db.CreateCommand();
        consume.Transaction = tx;
        consume.CommandText = """
            UPDATE activation_challenges SET consumed_at = $now
            WHERE id = $id AND consumed_at IS NULL
            """;
        consume.Parameters.AddWithValue("$id", challengeId!);
        consume.Parameters.AddWithValue("$now", now);
        if (consume.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Activation challenge was not consumed.");

        tx.Commit();
        return licenseId;
    }

    public const long MaximumCredits = 1_000_000_000;

    public CreditGrantResult GrantCredits(Guid licenseId, Guid grantId, long credits)
    {
        if (grantId == Guid.Empty || credits is < 1 or > MaximumCredits)
            return CreditGrantResult.Invalid;

        var id = licenseId.ToString("N");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var existing = db.CreateCommand();
        existing.Transaction = tx;
        existing.CommandText = """
            SELECT credits FROM credit_grants WHERE license_id = $id AND grant_id = $grant
            """;
        existing.Parameters.AddWithValue("$id", id);
        existing.Parameters.AddWithValue("$grant", grantId.ToString("N"));
        if (existing.ExecuteScalar() is long previous)
            return previous == credits ? CreditGrantResult.Applied : CreditGrantResult.Conflict;

        using var balance = db.CreateCommand();
        balance.Transaction = tx;
        balance.CommandText = """
            SELECT available_credits + coalesce((
                SELECT sum(reserved) FROM analysis_reservations
                WHERE license_id = $id AND actual IS NULL
            ), 0)
            FROM licenses WHERE id = $id AND revoked_at IS NULL
            """;
        balance.Parameters.AddWithValue("$id", id);
        if (balance.ExecuteScalar() is not long available)
            return CreditGrantResult.NotFound;
        if (available > MaximumCredits - credits)
            return CreditGrantResult.Conflict;

        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE licenses SET available_credits = available_credits + $amount WHERE id = $id;
            INSERT INTO credit_grants (license_id, grant_id, credits, granted_at)
            VALUES ($id, $grant, $amount, $now);
            """;
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$amount", credits);
        update.Parameters.AddWithValue("$grant", grantId.ToString("N"));
        update.Parameters.AddWithValue("$now", Now());
        update.ExecuteNonQuery();
        tx.Commit();
        return CreditGrantResult.Applied;
    }

    public long? ReadAvailableCredits(Guid licenseId, long timestamp, byte[]? signature)
    {
        var now = Now();
        if (timestamp < now - 60 || timestamp > now + 60 || signature is not { Length: 64 })
            return null;

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT device_public_key, available_credits FROM licenses
            WHERE id = $id AND revoked_at IS NULL AND activated_at IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("$id", licenseId.ToString("N"));
        using var row = cmd.ExecuteReader();
        return row.Read() && row[0] is byte[] key
            && DeviceActivationProof.VerifyLicenseStatus(key, licenseId, timestamp, signature)
            ? row.GetInt64(1)
            : null;
    }

    public bool Revoke(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE licenses SET revoked_at = max($now, coalesce(activated_at, issued_at)),
                activation_code_hash = NULL
            WHERE id = $id AND revoked_at IS NULL
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", Now());
        return cmd.ExecuteNonQuery() == 1;
    }

    private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string ReadSql(string file)
    {
        using var stream = typeof(LicenseStore).Assembly.GetManifestResourceStream(
            $"ScreenAssistant.Server.Storage.{file}")
            ?? throw new InvalidOperationException($"SQL resource missing: {file}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed record IssuedLicense(string LicenseId, string ActivationCode);
internal sealed record ActivationChallenge(string ChallengeId, byte[] Nonce, DateTimeOffset ExpiresAt);

internal enum CreditGrantResult { Applied, Invalid, NotFound, Conflict }
