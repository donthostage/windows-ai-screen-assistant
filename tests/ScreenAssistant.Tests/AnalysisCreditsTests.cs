using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Licensing;
using ScreenAssistant.Server;

namespace ScreenAssistant.Tests;

public sealed class AnalysisCreditsTests
{
    [Fact]
    public async Task ReservationsSerializeBudgetAndSettleOnlyOnce()
    {
        var folder = Directory.CreateTempSubdirectory("screen-assistant-credits-");
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
            var store = new LicenseStore(Path.Combine(folder.FullName, "test.db"), clock);
            store.Initialize();
            var license = store.Issue();
            var id = Guid.ParseExact(license.LicenseId, "N");
            using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var challenge = Assert.IsType<ActivationChallenge>(
                store.Start(license.ActivationCode, device.ExportSubjectPublicKeyInfo()));
            Assert.Equal(license.LicenseId,
                store.Complete(challenge.ChallengeId, DeviceActivationProof.Sign(device, challenge.Nonce)));

            Assert.Equal(CreditGrantResult.Applied, store.GrantCredits(id, Guid.NewGuid(), 10));
            var hash = SHA256.HashData("request"u8);
            var operations = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var results = await Task.WhenAll(operations.Select(operation =>
                Task.Run(() => store.ReserveAnalysis(id, operation, hash, 8))));
            Assert.Single(results, result => result == ReservationResult.Reserved);
            Assert.Single(results, result => result == ReservationResult.Insufficient);
            var winner = operations[Array.IndexOf(results, ReservationResult.Reserved)];

            Assert.Equal(ReservationResult.Duplicate, store.ReserveAnalysis(id, winner, hash, 8));
            Assert.Equal(ReservationResult.Conflict,
                store.ReserveAnalysis(id, winner, SHA256.HashData("different"u8), 8));
            Assert.Equal(CreditGrantResult.Conflict,
                store.GrantCredits(id, Guid.NewGuid(), LicenseStore.MaximumCredits - 2));
            Assert.False(store.SettleAnalysis(id, winner, 9));
            Assert.True(store.SettleAnalysis(id, winner, 3));
            Assert.False(store.SettleAnalysis(id, winner, 0));

            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            Assert.Equal(7L, store.ReadAvailableCredits(id, now,
                DeviceActivationProof.SignLicenseStatus(device, id, now)));

            var operation = Guid.NewGuid();
            byte[] frame = [1, 2, 3];
            const string mediaType = "image/png";
            var request = new AnalysisRequest(id, operation, now,
                DeviceActivationProof.SignAnalysis(device, id, operation, now,
                    DeviceActivationProof.HashAnalysisFrame(mediaType, frame)), mediaType, frame);
            var calls = 0;
            Task<string> Provider(IReadOnlyList<ImagePayload> payload, bool series)
            {
                calls++;
                Assert.Equal(frame, payload[0].Bytes);
                Assert.Equal(series ? 2 : 1, payload.Count);
                return Task.FromResult("Ответ");
            }

            async Task<int?> Send(AnalysisRequest input) =>
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(
                    await AnalysisEndpoint.RunAsync(input, store, Provider, NullLogger.Instance, 100)).StatusCode;

            Assert.Equal(401, await Send(request with { Frame = [1, 2, 4] }));
            Assert.Equal(200, await Send(request));
            Assert.Equal(409, await Send(request));
            Assert.Equal(1, calls);
            ImagePayload[] frames = [new(mediaType, frame), new("image/jpeg", [4, 5])];
            var seriesId = Guid.NewGuid();
            var series = new AnalysisRequest(id, seriesId, now,
                DeviceActivationProof.SignAnalysis(device, id, seriesId, now, AnalysisFrames.HashSeries(frames)),
                null, null, frames);
            Assert.Equal(401, await Send(series with { Frames = [frames[1], frames[0]] }));
            Assert.Equal(400, await Send(series with { Frame = frame }));
            Assert.Equal(200, await Send(series));
            Assert.Equal(409, await Send(series));
            Assert.Equal(2, calls);
            Assert.Equal(4L, store.ReadAvailableCredits(id, now,
                DeviceActivationProof.SignLicenseStatus(device, id, now)));

            var uncertain = Guid.NewGuid();
            Assert.Equal(ReservationResult.Reserved, store.ReserveAnalysis(id, uncertain, hash, 4));
            Assert.Empty(store.ListPendingAnalysis());
            Assert.False(store.SettleAnalysis(id, uncertain, 0, byOperator: true));

            clock.Advance(TimeSpan.FromMinutes(2));
            var pending = Assert.Single(store.ListPendingAnalysis());
            Assert.Equal(uncertain, pending.OperationId);
            Assert.Equal(4L, pending.ReservedCredits);
            Assert.True(store.SettleAnalysis(id, uncertain, 1, byOperator: true));
            Assert.False(store.SettleAnalysis(id, uncertain, 1, byOperator: true));
            Assert.Empty(store.ListPendingAnalysis());
            var manual = Assert.Single(store.ReadOperatorAudit(0), entry => entry.Action == "analysis-settled");
            Assert.Equal(uncertain, manual.ReferenceId);
            Assert.Equal(1L, manual.Credits);
            now = clock.GetUtcNow().ToUnixTimeSeconds();
            Assert.Equal(3L, store.ReadAvailableCredits(id, now,
                DeviceActivationProof.SignLicenseStatus(device, id, now)));

            var budgetOperation = Guid.NewGuid();
            Assert.Equal(ReservationResult.DailyLimit, store.ReserveAnalysis(id, budgetOperation, hash, 1, dailyLimit: 7));
            clock.Advance(TimeSpan.FromDays(1));
            Assert.Equal(ReservationResult.Reserved, store.ReserveAnalysis(id, budgetOperation, hash, 1, dailyLimit: 7));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in folder.EnumerateFiles())
                file.Delete();
            folder.Delete();
        }
    }
}
