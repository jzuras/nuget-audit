using System.Text.Json;
using System.Text.Json.Serialization;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Configuration;

/// <summary>
/// Loads TrustConfig.json from disk using System.Text.Json.
/// Returns a default configuration when the file is absent and LoadOrDefault is called.
/// </summary>
public class TrustConfigLoader : ITrustConfigLoader
{
    #region Constants

    /// <summary>
    /// Gets the default recent-days threshold for supply chain freshness warnings.
    /// </summary>
    public static int DefaultRecentDaysThreshold { get; } = 14;

    private static JsonSerializerOptions JsonOptions => CoreJsonOptions.CaseInsensitive;

    #endregion

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the JSON cannot be deserialized into a TrustConfig.</exception>
    public TrustConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"TrustConfig.json not found at '{path}'.", path);
        }

        string json = File.ReadAllText(path);

        var dto = JsonSerializer.Deserialize<TrustConfigDto>(json, TrustConfigLoader.JsonOptions)
            ?? throw new InvalidDataException(
                $"TrustConfig.json at '{path}' could not be deserialized — the file may be empty or malformed.");

        return BuildConfig(dto);
    }

    /// <inheritdoc />
    public (TrustConfig Config, bool FileFound) LoadOrDefault(string path)
    {
        if (!File.Exists(path))
        {
            return (new TrustConfig([], [], TrustConfigLoader.DefaultRecentDaysThreshold), false);
        }

        return (this.Load(path), true);
    }

    /// <summary>
    /// Converts a deserialized DTO into a <see cref="TrustConfig"/> record.
    /// </summary>
    /// <param name="dto">The deserialized DTO from JSON.</param>
    /// <returns>A fully constructed <see cref="TrustConfig"/>.</returns>
    private static TrustConfig BuildConfig(TrustConfigDto dto)
    {
        string[] owners = dto.TrustedOwners ?? [];

        TrustedPackageEntry[] packages = (dto.TrustedPackages ?? [])
            .Select(p => new TrustedPackageEntry(
                p.Id ?? string.Empty,
                p.Version ?? string.Empty))
            .ToArray();

        int threshold = dto.RecentDaysThreshold ?? TrustConfigLoader.DefaultRecentDaysThreshold;

        return new TrustConfig(owners, packages, threshold);
    }

    #region Private DTOs

    /// <summary>
    /// Internal DTO for deserializing TrustConfig.json.
    /// All properties are nullable to handle missing fields gracefully.
    /// </summary>
    private sealed class TrustConfigDto
    {
        /// <summary>Gets or sets the trusted owner account names.</summary>
        [JsonPropertyName("trustedOwners")]
        public string[]? TrustedOwners { get; init; }

        /// <summary>Gets or sets the explicitly trusted package entries.</summary>
        [JsonPropertyName("trustedPackages")]
        public TrustedPackageEntryDto[]? TrustedPackages { get; init; }

        /// <summary>Gets or sets the recent-days threshold for supply chain freshness warnings.</summary>
        [JsonPropertyName("recentDaysThreshold")]
        public int? RecentDaysThreshold { get; init; }
    }

    /// <summary>
    /// Internal DTO for deserializing a single TrustedPackages entry.
    /// </summary>
    private sealed class TrustedPackageEntryDto
    {
        /// <summary>Gets or sets the NuGet package identifier.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        /// <summary>Gets or sets the trusted version string.</summary>
        [JsonPropertyName("version")]
        public string? Version { get; init; }
    }

    #endregion
}
