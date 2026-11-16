using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ScreenAssistant.Core.Licensing;
using ScreenAssistant.Server;

namespace ScreenAssistant.Tests;

public sealed class LicenseDatabaseBackupTests
{
    [Fact]
    public void SnapshotRestoresDeviceBindingBalanceAndPendingReservation()
    {
        var directory = Directory.CreateTempSubdirectory("screen-assistant-backup-test-");
        try
        {
            var source = Path.Combine(directory.FullName, "live.db");
            var backup = Path.Combine(directory.FullName, "snapshot.db");
            var store = new LicenseStore(source, TimeProvider.System);
            store.Initialize();
            var issued = store.Issue();
            var id = Guid.ParseExact(issued.LicenseId, "N");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var challenge = Assert.IsType<ActivationChallenge>(
                store.Start(issued.ActivationCode, key.ExportSubjectPublicKeyInfo()));
            Assert.Equal(issued.LicenseId,
                store.Complete(challenge.ChallengeId, DeviceActivationProof.Sign(key, challenge.Nonce)));
            Assert.Equal(CreditGrantResult.Applied, store.GrantCredits(id, Guid.NewGuid(), 10));
            var operation = Guid.NewGuid();
            var hash = SHA256.HashData("backup test"u8);
            Assert.Equal(ReservationResult.Reserved, store.ReserveAnalysis(id, operation, hash, 4));

            LicenseDatabaseBackup.Create(source, backup);
            Assert.Throws<IOException>(() => LicenseDatabaseBackup.Create(source, backup));
            Assert.True(store.Revoke(issued.LicenseId));

            var restored = new LicenseStore(backup, TimeProvider.System);
            restored.Initialize();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signature = DeviceActivationProof.SignLicenseStatus(key, id, now);
            Assert.Equal(6L, restored.ReadAvailableCredits(id, now, signature));
            Assert.Null(store.ReadAvailableCredits(id, now, signature));
            Assert.Equal(ReservationResult.Duplicate, restored.ReserveAnalysis(id, operation, hash, 4));
            Assert.True(restored.SettleAnalysis(id, operation, 1));
            Assert.Equal(9L, restored.ReadAvailableCredits(id, now, signature));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in directory.EnumerateFiles())
                file.Delete();
            directory.Delete();
        }
    }
}
