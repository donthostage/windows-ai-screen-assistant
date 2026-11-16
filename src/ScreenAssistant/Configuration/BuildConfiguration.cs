using System.Reflection;
using ScreenAssistant.Core.Licensing;

namespace ScreenAssistant.Configuration;

internal static class BuildConfiguration
{
    internal static bool TryGetLicenseServer(out Uri? server)
    {
        var value = typeof(BuildConfiguration).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "LicenseServerUrl")?.Value ?? "";
        return ActivationClient.TryParseServer(value, out server);
    }
}
