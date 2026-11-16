using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ScreenAssistant.Core.Licensing;

public static class DeviceActivationProof
{
    private const int ChallengeLength = 32;
    private const int SignatureLength = 64;
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private static ReadOnlySpan<byte> Domain => "ScreenAssistant/device-activation/v1\0"u8;

    public static byte[] CreateChallenge() => RandomNumberGenerator.GetBytes(ChallengeLength);

    public static byte[] Sign(ECDsa deviceKey, ReadOnlySpan<byte> challenge)
        => SignMessage(deviceKey, BuildMessage(challenge));

    public static byte[] SignLicenseStatus(ECDsa deviceKey, Guid licenseId, long timestamp)
        => SignMessage(deviceKey, BuildStatusMessage(licenseId, timestamp));

    public static bool VerifyLicenseStatus(ReadOnlySpan<byte> publicKey, Guid licenseId,
        long timestamp, ReadOnlySpan<byte> signature)
        => VerifyMessage(publicKey, BuildStatusMessage(licenseId, timestamp), signature);

    public static byte[] SignAnalysis(ECDsa deviceKey, Guid licenseId, Guid operationId,
        long timestamp, ReadOnlySpan<byte> requestHash)
        => SignMessage(deviceKey, BuildAnalysisMessage(licenseId, operationId, timestamp, requestHash));

    public static bool VerifyAnalysis(ReadOnlySpan<byte> publicKey, Guid licenseId, Guid operationId,
        long timestamp, ReadOnlySpan<byte> requestHash, ReadOnlySpan<byte> signature)
        => requestHash.Length == 32
            && VerifyMessage(publicKey, BuildAnalysisMessage(licenseId, operationId, timestamp, requestHash), signature);

    public static byte[] HashAnalysisFrame(string mediaType, ReadOnlySpan<byte> frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(mediaType));
        hash.AppendData([0]);
        hash.AppendData(frame);
        return hash.GetHashAndReset();
    }

    private static byte[] BuildAnalysisMessage(Guid licenseId, Guid operationId, long timestamp,
        ReadOnlySpan<byte> requestHash)
    {
        if (requestHash.Length != 32)
            throw new ArgumentException("A SHA-256 request hash is required.", nameof(requestHash));

        ReadOnlySpan<byte> domain = "ScreenAssistant/analysis/v1\0"u8;
        var message = new byte[domain.Length + 16 + 16 + sizeof(long) + 32];
        domain.CopyTo(message);
        var payload = message.AsSpan(domain.Length);
        licenseId.TryWriteBytes(payload, bigEndian: true, out _);
        operationId.TryWriteBytes(payload[16..], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(payload[32..], timestamp);
        requestHash.CopyTo(payload[40..]);
        return message;
    }

    private static byte[] BuildStatusMessage(Guid licenseId, long timestamp)
    {
        ReadOnlySpan<byte> domain = "ScreenAssistant/license-status/v1\0"u8;
        var message = new byte[domain.Length + 16 + sizeof(long)];
        domain.CopyTo(message);
        licenseId.TryWriteBytes(message.AsSpan(domain.Length), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(domain.Length + 16), timestamp);
        return message;
    }

    private static byte[] SignMessage(ECDsa deviceKey, ReadOnlySpan<byte> message)
    {
        if (deviceKey.ExportParameters(false).Curve.Oid.Value != P256Oid)
            throw new ArgumentException("A P-256 device key is required.", nameof(deviceKey));

        return deviceKey.SignData(message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> signature)
        => challenge.Length == ChallengeLength && VerifyMessage(publicKey, BuildMessage(challenge), signature);

    private static bool VerifyMessage(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength
            || publicKey.IsEmpty || publicKey.Length > 128)
            return false;

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            return consumed == publicKey.Length && key.ExportParameters(false).Curve.Oid.Value == P256Oid
                && key.VerifyData(message, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] BuildMessage(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != ChallengeLength)
            throw new ArgumentException("A 32-byte activation challenge is required.", nameof(challenge));

        var message = new byte[Domain.Length + ChallengeLength];
        Domain.CopyTo(message);
        challenge.CopyTo(message.AsSpan(Domain.Length));
        return message;
    }
}
