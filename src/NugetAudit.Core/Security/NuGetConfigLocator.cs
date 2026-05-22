using System.Runtime.InteropServices;

namespace NugetAudit.Core.Security;

/// <summary>
/// Provides the cross-platform path to the global NuGet configuration file.
/// </summary>
public static class NuGetConfigLocator
{
    /// <summary>
    /// Returns the path to the global NuGet.Config file for the current platform.
    /// On Windows: <c>%APPDATA%\NuGet\NuGet.Config</c>.
    /// On macOS/Linux: <c>~/.nuget/NuGet/NuGet.Config</c>.
    /// </summary>
    /// <returns>The full path to the global NuGet.Config file.</returns>
    public static string GetGlobalNuGetConfigPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NuGet",
                "NuGet.Config");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "NuGet",
            "NuGet.Config");
    }
}
