using System.Security.Cryptography;
using ScreenAssistant.Core.Licensing;

namespace ScreenAssistant.Tests;

public class DeviceActivationProofTests
{
    [Fact]
    public void ProofIsBoundToTheDeviceAndChallenge()
    {
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherDevice = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = device.ExportSubjectPublicKeyInfo();
        var challenge = DeviceActivationProof.CreateChallenge();
        var proof = DeviceActivationProof.Sign(device, challenge);
        var otherChallenge = (byte[])challenge.Clone();
        otherChallenge[0] ^= 1;

        Assert.True(DeviceActivationProof.Verify(publicKey, challenge, proof));
        Assert.False(DeviceActivationProof.Verify(publicKey, otherChallenge, proof));
        Assert.False(DeviceActivationProof.Verify(otherDevice.ExportSubjectPublicKeyInfo(), challenge, proof));
        Assert.False(DeviceActivationProof.Verify([.. publicKey, 0], challenge, proof));
        Assert.False(DeviceActivationProof.Verify([1, 2, 3], challenge, proof));
        Assert.False(DeviceActivationProof.Verify(publicKey, challenge, proof.AsSpan(1)));
    }
}
