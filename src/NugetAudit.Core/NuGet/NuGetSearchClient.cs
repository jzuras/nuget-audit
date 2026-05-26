using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.NuGet;

/// <summary>
/// Queries the NuGet Search API to obtain publisher verification status and owner account names.
/// The Search API is the only source of the nuget.org verified (blue shield) flag.
/// </summary>
/// <remarks>
/// Only queries the public nuget.org search endpoint. Private feed packages skip this client
/// and are assigned PrivateFeed trust status by the audit runner without a search lookup.
/// </remarks>
public class NuGetSearchClient : INuGetSearchClient
{
    #region Constants

    /// <summary>
    /// Gets the nuget.org search query endpoint URL.
    /// </summary>
    private static string SearchQueryUrl { get; } =
        "https://azuresearch-usnc.nuget.org/query";

    private static JsonSerializerOptions JsonOptions => CoreJsonOptions.CaseInsensitive;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the HTTP client used to call the NuGet Search API.
    /// </summary>
    private HttpClient HttpClient { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetSearchClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for search API calls.</param>
    public NuGetSearchClient(HttpClient httpClient)
    {
        this.HttpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<SearchResult?> SearchPackageAsync(string packageId, CancellationToken ct)
    {
        try
        {
            var match = await this.QuerySearchApiAsync(packageId, prerelease: true, ct);

            if (match is null)
            {
                return null;
            }

            return new SearchResult(
                Verified: match.Verified,
                Owners: match.Owners ?? [],
                TotalDownloads: match.TotalDownloads);
        }
        catch
        {
            // Search failures are non-fatal: treat as "not found on nuget.org".
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct)
    {
        try
        {
            // Query with prerelease=false to get only stable versions, matching PS Resolve-VersionRange.
            var match = await this.QuerySearchApiAsync(packageId, prerelease: false, ct);
            return match?.Version;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Queries the NuGet Search API and returns the first matching item for a given package ID.
    /// </summary>
    /// <param name="packageId">The package identifier to search for.</param>
    /// <param name="prerelease">When true, include pre-release versions in results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The first matching item, or null if not found.</returns>
    private async Task<SearchApiItem?> QuerySearchApiAsync(
        string packageId,
        bool prerelease,
        CancellationToken ct)
    {
        string encodedId = Uri.EscapeDataString(packageId);
        var url = new Uri($"{NuGetSearchClient.SearchQueryUrl}?q=packageid:{encodedId}&take=1&prerelease={prerelease.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}&semVerLevel=2.0.0");

        using var response = await this.HttpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        var apiResponse = JsonSerializer.Deserialize<SearchApiResponse>(json, NuGetSearchClient.JsonOptions);

        return apiResponse?.Data?.FirstOrDefault(d =>
            string.Equals(d.Id, packageId, StringComparison.OrdinalIgnoreCase));
    }

    #region Private DTOs

    /// <summary>
    /// Top-level response from the NuGet Search API query endpoint.
    /// </summary>
    private sealed class SearchApiResponse
    {
        /// <summary>Gets or sets the list of search result items.</summary>
        [JsonPropertyName("data")]
        public SearchApiItem[]? Data { get; init; }
    }

    /// <summary>
    /// A single item returned by the NuGet Search API.
    /// </summary>
    private sealed class SearchApiItem
    {
        /// <summary>Gets or sets the package identifier.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        /// <summary>Gets or sets the latest version of the package (stable or pre-release, depending on query).</summary>
        [JsonPropertyName("version")]
        public string? Version { get; init; }

        /// <summary>Gets or sets whether the package publisher has a verified prefix reservation.</summary>
        [JsonPropertyName("verified")]
        public bool Verified { get; init; }

        /// <summary>Gets or sets the owner account names.</summary>
        [JsonPropertyName("owners")]
        public string[]? Owners { get; init; }

        /// <summary>Gets or sets the total download count across all versions.</summary>
        [JsonPropertyName("totalDownloads")]
        public long TotalDownloads { get; init; }
    }

    #endregion
}
