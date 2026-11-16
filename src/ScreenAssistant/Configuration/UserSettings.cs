using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Geometry;

namespace ScreenAssistant.Configuration;

internal sealed record UserSettings
{
    public string BaseUrl { get; init; } = AiClient.DefaultBaseUrl;
    public string Model { get; init; } = "";
    public int IntervalSeconds { get; init; } = 30;
    public int DurationMinutes { get; init; } = 120;
    public bool SkipIdenticalFrames { get; init; } = true;
    public bool UseStreaming { get; init; } = true;
    public int? CropX { get; init; }
    public int? CropY { get; init; }
    public int? CropWidth { get; init; }
    public int? CropHeight { get; init; }

    public CropRect? GetStoredCrop() =>
        FrameGeometry.TryReadStored(CropX, CropY, CropWidth, CropHeight);

    public CropRect? GetCropForFrame(int frameWidth, int frameHeight)
    {
        var stored = GetStoredCrop();
        if (stored is null)
            return null;
        return FrameGeometry.NormalizeOrNull(stored.Value, frameWidth, frameHeight);
    }

    public UserSettings WithCrop(CropRect? crop) => this with
    {
        CropX = crop?.X,
        CropY = crop?.Y,
        CropWidth = crop?.Width,
        CropHeight = crop?.Height
    };

    public static UserSettings Load()
    {
        if (!File.Exists(LocalFiles.Settings))
            return new();

        var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(LocalFiles.Settings))
            ?? new UserSettings();
        return loaded with
        {
            IntervalSeconds = Math.Clamp(loaded.IntervalSeconds, 5, 3600),
            DurationMinutes = Math.Clamp(loaded.DurationMinutes, 1, 720)
        };
    }

    public void Save() => LocalFiles.WriteAtomically(LocalFiles.Settings,
        JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
}

internal static class KeyStore
{
    public static string Read(Uri endpoint)
    {
        var path = KeyPath(endpoint);
        if (!File.Exists(path))
            return "";

        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static void Save(Uri endpoint, string value)
    {
        var path = KeyPath(endpoint);
        if (value.Length == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            LocalFiles.WriteAtomically(path,
                ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string KeyPath(Uri endpoint)
    {
        var endpointId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));
        return Path.Combine(LocalFiles.DirectoryPath, $"key-{endpointId}.dpapi");
    }
}

internal static class LocalFiles
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenAssistant");
    public static readonly string Settings = Path.Combine(DirectoryPath, "settings.json");

    public static void WriteAtomically(string path, byte[] contents)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static void Log(string operation, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var log = Path.Combine(DirectoryPath, "errors.log");
            if (File.Exists(log) && new FileInfo(log).Length >= 256 * 1024)
                File.Move(log, log + ".previous", overwrite: true);

            var cause = ex.InnerException ?? ex;
            var httpStatus = ex is AiException { Status: { } status } ? $" HTTP {(int)status}" : "";
            File.AppendAllText(log,
                $"{DateTimeOffset.Now:O} {operation} {cause.GetType().Name} 0x{cause.HResult:X8}{httpStatus}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
