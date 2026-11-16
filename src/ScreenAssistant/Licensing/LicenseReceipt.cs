using System.IO;
using System.Text.Json;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.Licensing;

namespace ScreenAssistant.Licensing;

internal sealed record LicenseReceipt(string Server, Guid LicenseId, bool HardwareBacked)
{
    private static readonly string FilePath = Path.Combine(LocalFiles.DirectoryPath, "license.json");

    internal static LicenseReceipt? Load()
    {
        if (!File.Exists(FilePath))
            return null;

        var receipt = JsonSerializer.Deserialize<LicenseReceipt>(File.ReadAllBytes(FilePath));
        if (receipt is null || receipt.LicenseId == Guid.Empty || string.IsNullOrEmpty(receipt.Server)
            || !ActivationClient.TryParseServer(receipt.Server, out _))
            throw new InvalidDataException("Invalid local license receipt.");

        return receipt;
    }

    internal void Save() => LocalFiles.WriteAtomically(FilePath, JsonSerializer.SerializeToUtf8Bytes(this));
}
