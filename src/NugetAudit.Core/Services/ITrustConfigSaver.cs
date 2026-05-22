using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Saves TrustConfig.json to disk. Used by the Blazor GUI trust-config editor page.
/// </summary>
public interface ITrustConfigSaver
{
    /// <summary>
    /// Serializes and writes a trust configuration to the specified path.
    /// </summary>
    /// <param name="config">The trust configuration to save.</param>
    /// <param name="path">The destination file path for TrustConfig.json.</param>
    void Save(TrustConfig config, string path);
}
