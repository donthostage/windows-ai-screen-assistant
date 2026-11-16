using System.Security.Cryptography;

namespace ScreenAssistant.Licensing;

internal static class WindowsDeviceKey
{
    private const string KeyName = "ScreenAssistant.DeviceActivation.v1";

    internal static ECDsaCng OpenOrCreate(bool hardwareBacked)
    {
        var provider = hardwareBacked
            ? CngProvider.MicrosoftPlatformCryptoProvider
            : CngProvider.MicrosoftSoftwareKeyStorageProvider;

        if (!CngKey.Exists(KeyName, provider))
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = provider,
                KeyUsage = CngKeyUsages.Signing,
                ExportPolicy = CngExportPolicies.None
            };

            try
            {
                using var created = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, parameters);
            }
            catch (CryptographicException) when (CngKey.Exists(KeyName, provider))
            {
                // Another instance created the same named key.
            }
        }

        using var key = CngKey.Open(KeyName, provider);
        return new ECDsaCng(key);
    }
}
