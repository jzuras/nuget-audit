using System.Text.Json;
using System.Text.Json.Serialization;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Configuration;

/// <summary>
/// Saves TrustConfig.json to disk using System.Text.Json.
/// Produces camelCase JSON matching the format expected by TrustConfigLoader and the CLI.
/// Used by the Blazor GUI trust-config editor page.
/// </summary>
public class TrustConfigSaver : ITrustConfigSaver
{
    /// <summary>
    /// Gets the JSON serialization options used when writing TrustConfig.json.
    /// </summary>
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
    };

    /// <inheritdoc />
    public void Save(TrustConfig config, string path)
    {
        var dto = new TrustConfigDto(
            config.TrustedOwners,
            config.TrustedPackages
                .Select(p => new TrustedPackageEntryDto(p.Id, p.Version))
                .ToArray(),
            config.RecentDaysThreshold);

        string json = JsonSerializer.Serialize(dto, TrustConfigSaver.JsonOptions);

        string? directory = Path.GetDirectoryName(path);

        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, json);
    }

    #region Private DTOs

    /// <summary>
    /// Internal DTO for serializing TrustConfig.json in camelCase format.
    /// </summary>
    private sealed class TrustConfigDto
    {
        /// <summary>Gets the trusted owner account names.</summary>
        [JsonPropertyName("trustedOwners")]
        public string[] TrustedOwners { get; }

        /// <summary>Gets the explicitly trusted package entries.</summary>
        [JsonPropertyName("trustedPackages")]
        public TrustedPackageEntryDto[] TrustedPackages { get; }

        /// <summary>Gets the recent-days threshold for supply chain freshness warnings.</summary>
        [JsonPropertyName("recentDaysThreshold")]
        public int RecentDaysThreshold { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TrustConfigDto"/> class.
        /// </summary>
        /// <param name="trustedOwners">The trusted owner account names.</param>
        /// <param name="trustedPackages">The explicitly trusted package entries.</param>
        /// <param name="recentDaysThreshold">The recent-days threshold.</param>
        public TrustConfigDto(
            string[] trustedOwners,
            TrustedPackageEntryDto[] trustedPackages,
            int recentDaysThreshold)
        {
            this.TrustedOwners = trustedOwners;
            this.TrustedPackages = trustedPackages;
            this.RecentDaysThreshold = recentDaysThreshold;
        }
    }

    /// <summary>
    /// Internal DTO for serializing a single TrustedPackages entry.
    /// </summary>
    private sealed class TrustedPackageEntryDto
    {
        /// <summary>Gets the NuGet package identifier.</summary>
        [JsonPropertyName("id")]
        public string Id { get; }

        /// <summary>Gets the trusted version string.</summary>
        [JsonPropertyName("version")]
        public string Version { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TrustedPackageEntryDto"/> class.
        /// </summary>
        /// <param name="id">The NuGet package identifier.</param>
        /// <param name="version">The trusted version string.</param>
        public TrustedPackageEntryDto(string id, string version)
        {
            this.Id = id;
            this.Version = version;
        }
    }

    #endregion
}
