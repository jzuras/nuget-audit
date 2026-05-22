using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Loads TrustConfig.json from disk.
/// </summary>
public interface ITrustConfigLoader
{
    /// <summary>
    /// Loads a TrustConfig.json file from the specified path.
    /// Throws if the file does not exist or is malformed.
    /// </summary>
    /// <param name="path">Path to the TrustConfig.json file.</param>
    /// <returns>The loaded trust configuration.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    TrustConfig Load(string path);

    /// <summary>
    /// Loads a TrustConfig.json file from the specified path, returning an empty configuration if the file does not exist.
    /// </summary>
    /// <param name="path">Path to the TrustConfig.json file.</param>
    /// <returns>
    /// A tuple of the trust configuration and a flag indicating whether the file was found.
    /// When the file is absent the configuration has empty trusted owners and packages.
    /// </returns>
    (TrustConfig Config, bool FileFound) LoadOrDefault(string path);
}
