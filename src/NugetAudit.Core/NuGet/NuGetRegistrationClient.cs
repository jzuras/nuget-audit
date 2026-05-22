using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NugetAudit.Core;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;
using NugetAudit.Core.Trust;

namespace NugetAudit.Core.NuGet;

/// <summary>
/// Fetches package metadata and dependency information from the NuGet Registration v3 API.
/// Handles pagination, gzip decompression (via HttpClient handler config), Basic/Bearer auth headers,
/// and maps 404 responses to the PrivateFeed outcome.
/// </summary>
/// <remarks>
/// The injected <see cref="HttpClient"/> should be created with
/// <c>AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate</c>
/// on its handler to support the registration5-gz-semver2 endpoint on nuget.org.
/// </remarks>
public class NuGetRegistrationClient : INuGetRegistrationClient
{
    #region Fields / Properties

    private static JsonSerializerOptions JsonOptions => CoreJsonOptions.CaseInsensitive;

    /// <summary>
    /// Gets the HTTP client used to call the NuGet Registration API.
    /// </summary>
    private HttpClient HttpClient { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetRegistrationClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client to use. Should be configured with GZip automatic decompression
    /// for compatibility with the nuget.org registration5-gz-semver2 endpoint.
    /// </param>
    public NuGetRegistrationClient(HttpClient httpClient)
    {
        this.HttpClient = httpClient;
    }

    #region Public Interface

    /// <inheritdoc />
    public async Task<PackageMetadataResult> GetPackageMetadataAsync(
        string packageId,
        string version,
        bool fallbackToLatest,
        string baseUrl,
        FeedCredential? credential,
        CancellationToken ct)
    {
        try
        {
            string packageIdLower = packageId.ToLowerInvariant();
            string indexUrl = $"{baseUrl.TrimEnd('/')}/{packageIdLower}/index.json";

            RegistrationIndex? index = await this.FetchIndexAsync(indexUrl, credential, ct);

            if (index is null || index.Items is null || index.Items.Length == 0)
            {
                return new PackageMetadataResult(
                    RegistrationOutcome.Error,
                    null,
                    $"Registration index for '{packageId}' returned no pages.");
            }

            RegistrationCatalogEntry? catalogEntry =
                await this.FindVersionAsync(index, version, credential, ct);

            if (catalogEntry is not null)
            {
                return BuildFoundResult(packageId, catalogEntry);
            }

            // Version not found in registration pages.
            if (fallbackToLatest is true)
            {
                catalogEntry = await this.GetLatestCatalogEntryAsync(index, credential, ct);

                if (catalogEntry is not null)
                {
                    return BuildFoundResult(packageId, catalogEntry);
                }

                return new PackageMetadataResult(
                    RegistrationOutcome.Error,
                    null,
                    $"Could not resolve latest version for '{packageId}'.");
            }

            // If the requested version falls outside the version range covered by the index,
            // it was never published to this feed — treat it as a private feed package.
            // This handles the case where a package ID later appears on nuget.org at a newer
            // version (e.g. v14) while the project still uses a version (e.g. v13) that was
            // always only on a private feed.
            if (IsVersionOutsideIndexRange(index, version))
            {
                return new PackageMetadataResult(RegistrationOutcome.PrivateFeed, null, null);
            }

            // Unlisted path: version falls within the feed's known range but is absent —
            // it was published and then delisted.
            var unlistedData = new PackageRegistrationData(
                PackageId: packageId,
                Version: version,
                Authors: null,
                Description: null,
                ProjectUrl: null,
                LicenseExpression: null,
                LicenseUrl: null,
                Published: null,
                IsDeprecated: false,
                HasVulnerabilities: false,
                IsUnlisted: true);

            return new PackageMetadataResult(RegistrationOutcome.Unlisted, unlistedData, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new PackageMetadataResult(RegistrationOutcome.PrivateFeed, null, null);
        }
        catch (Exception ex)
        {
            return new PackageMetadataResult(RegistrationOutcome.Error, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<PackageDependency[]> GetPackageDependenciesAsync(
        string packageId,
        string version,
        string targetFramework,
        string baseUrl,
        FeedCredential? credential,
        CancellationToken ct)
    {
        try
        {
            string packageIdLower = packageId.ToLowerInvariant();
            string indexUrl = $"{baseUrl.TrimEnd('/')}/{packageIdLower}/index.json";

            RegistrationIndex? index = await this.FetchIndexAsync(indexUrl, credential, ct);

            if (index is null || index.Items is null || index.Items.Length == 0)
            {
                return [];
            }

            RegistrationCatalogEntry? catalogEntry =
                await this.FindVersionAsync(index, version, credential, ct);

            if (catalogEntry is null || catalogEntry.DependencyGroups is null)
            {
                return [];
            }

            var selectedGroup = SelectDependencyGroup(catalogEntry.DependencyGroups, targetFramework);

            if (selectedGroup?.Dependencies is null)
            {
                return [];
            }

            return [.. selectedGroup.Dependencies
                .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                .Select(d => new PackageDependency(d.Id!, d.Range ?? string.Empty))];
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Private feed package — no dependencies resolvable.
            return [];
        }
        catch
        {
            return [];
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="version"/> is outside the
    /// version range covered by <paramref name="index"/> — i.e. below the first page's
    /// lower bound or above the last page's upper bound.
    /// A version outside this range was never published to the feed, so it should be
    /// treated as a private feed package rather than an unlisted one.
    /// Returns <see langword="false"/> when bounds are unavailable (safe fallback to Unlisted).
    /// </summary>
    /// <param name="index">The registration index whose pages define the known version range.</param>
    /// <param name="version">The version string to test.</param>
    private static bool IsVersionOutsideIndexRange(RegistrationIndex index, string version)
    {
        if (index.Items.Length == 0)
        {
            return false;
        }

        string? lowerBound = index.Items[0].Lower;
        string? upperBound = index.Items[^1].Upper;

        if (lowerBound is not null
            && SemanticVersionComparer.Compare(version, lowerBound) < 0)
        {
            return true;
        }

        if (upperBound is not null
            && SemanticVersionComparer.Compare(version, upperBound) > 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ordered netcoreapp TFMs tried after the net5.0+ descending walk, for packages
    /// that predate the unified net5.0+ naming scheme.
    /// </summary>
    private static string[] NetCoreAppFallbacks { get; } =
        ["netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.2", "netcoreapp2.1", "netcoreapp2.0"];

    /// <summary>
    /// Selects the most applicable dependency group for the given target framework.
    /// Priority (for net5.0+ TFMs):
    ///   exact match → descending net versions → netcoreapp shims
    ///   → netstandard2.1 → netstandard2.0 → no-framework catch-all → first group.
    /// This mirrors NuGet's TFM compatibility walk and avoids incorrectly falling back
    /// to netstandard2.0 when a closer net8.0/net9.0 group exists.
    /// </summary>
    /// <param name="groups">All dependency groups from the catalog entry.</param>
    /// <param name="targetFramework">The preferred target framework moniker.</param>
    /// <returns>The most applicable group, or null if no groups exist.</returns>
    private static RegistrationDependencyGroup? SelectDependencyGroup(
        RegistrationDependencyGroup[] groups,
        string targetFramework)
    {
        // 1. Exact match.
        var exact = groups.FirstOrDefault(g =>
            string.Equals(g.TargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        // 2. For net5.0+ TFMs walk down compatible versions before falling back to netstandard.
        //    e.g. net10.0 → net9.0 → net8.0 → ... → net5.0 → netcoreapp3.1 → netcoreapp3.0
        if (TryParseNetMajor(targetFramework, out int major))
        {
            for (int v = major - 1; v >= 5; v--)
            {
                var netMatch = groups.FirstOrDefault(g =>
                    string.Equals(g.TargetFramework, $"net{v}.0", StringComparison.OrdinalIgnoreCase));

                if (netMatch is not null)
                {
                    return netMatch;
                }
            }

            foreach (string coreapp in NuGetRegistrationClient.NetCoreAppFallbacks)
            {
                var coreMatch = groups.FirstOrDefault(g =>
                    string.Equals(g.TargetFramework, coreapp, StringComparison.OrdinalIgnoreCase));

                if (coreMatch is not null)
                {
                    return coreMatch;
                }
            }
        }

        // 3. netstandard2.1 before netstandard2.0 (2.1 is a superset).
        var ns21 = groups.FirstOrDefault(g =>
            string.Equals(g.TargetFramework, "netstandard2.1", StringComparison.OrdinalIgnoreCase));

        if (ns21 is not null)
        {
            return ns21;
        }

        // 4. netstandard2.0 — broadest compatible fallback for modern packages.
        var ns20 = groups.FirstOrDefault(g =>
            string.Equals(g.TargetFramework, "netstandard2.0", StringComparison.OrdinalIgnoreCase));

        if (ns20 is not null)
        {
            return ns20;
        }

        // 5. No-framework catch-all.
        var noFramework = groups.FirstOrDefault(g => string.IsNullOrWhiteSpace(g.TargetFramework));

        if (noFramework is not null)
        {
            return noFramework;
        }

        // 6. First available group.
        return groups.Length > 0 ? groups[0] : null;
    }

    /// <summary>
    /// Returns true when <paramref name="tfm"/> matches the "net{N}.0" pattern with N ≥ 5
    /// (i.e., the unified .NET naming scheme introduced in .NET 5).
    /// </summary>
    /// <param name="tfm">The target framework moniker to test.</param>
    /// <param name="major">The extracted major version number on success.</param>
    private static bool TryParseNetMajor(string tfm, out int major)
    {
        major = 0;

        // Must start with "net" and end with ".0" with digits in between.
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            || !tfm.EndsWith(".0", StringComparison.Ordinal)
            || tfm.Length <= 5)
        {
            return false;
        }

        string mid = tfm.Substring(3, tfm.Length - 5); // strip "net" prefix and ".0" suffix
        return int.TryParse(mid, out major) && major >= 5;
    }

    /// <summary>
    /// Fetches and deserializes the registration index for a package.
    /// Returns null on a 404 response (re-thrown as PrivateFeed in the caller).
    /// </summary>
    /// <param name="indexUrl">The full URL to the registration index JSON.</param>
    /// <param name="credential">Optional credentials for private feeds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized index, or null if not found.</returns>
    private async Task<RegistrationIndex?> FetchIndexAsync(
        string indexUrl,
        FeedCredential? credential,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, indexUrl);
        FeedCredentialHelper.AddCredentialHeaders(request, credential);

        using var response = await this.HttpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Caller maps this to PrivateFeed.
            throw new HttpRequestException(
                $"Package not found at '{indexUrl}'.",
                null,
                HttpStatusCode.NotFound);
        }

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);

        return JsonSerializer.Deserialize<RegistrationIndex>(json, NuGetRegistrationClient.JsonOptions);
    }

    /// <summary>
    /// Searches all registration pages for an entry matching the specified version.
    /// Fetches non-inlined pages only when the version falls within the page's lower/upper range.
    /// </summary>
    /// <param name="index">The registration index containing pages to search.</param>
    /// <param name="version">The version string to find.</param>
    /// <param name="credential">Optional credentials for fetching non-inlined pages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching catalog entry, or null if not found.</returns>
    private async Task<RegistrationCatalogEntry?> FindVersionAsync(
        RegistrationIndex index,
        string version,
        FeedCredential? credential,
        CancellationToken ct)
    {
        foreach (var page in index.Items)
        {
            if (page.Items is not null)
            {
                // Items are inlined in the index — no additional fetch needed.
                var match = page.Items.FirstOrDefault(i =>
                    string.Equals(
                        i.CatalogEntry?.Version,
                        version,
                        StringComparison.OrdinalIgnoreCase));

                if (match?.CatalogEntry is not null)
                {
                    return match.CatalogEntry;
                }
            }
            else if (page.Id is not null)
            {
                // Check version range before fetching the full page.
                bool inRange =
                    SemanticVersionComparer.Compare(version, page.Lower ?? string.Empty) >= 0
                    && SemanticVersionComparer.Compare(version, page.Upper ?? string.Empty) <= 0;

                if (inRange is false)
                {
                    continue;
                }

                var pageData = await this.FetchRegistrationPageAsync(page.Id, credential, ct);

                if (pageData?.Items is not null)
                {
                    var match = pageData.Items.FirstOrDefault(i =>
                        string.Equals(
                            i.CatalogEntry?.Version,
                            version,
                            StringComparison.OrdinalIgnoreCase));

                    if (match?.CatalogEntry is not null)
                    {
                        return match.CatalogEntry;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches the catalog entry for the latest available version of a package.
    /// Used when <c>fallbackToLatest</c> is true and the requested version is not found.
    /// </summary>
    /// <param name="index">The registration index.</param>
    /// <param name="credential">Optional credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The catalog entry for the latest version, or null if unavailable.</returns>
    private async Task<RegistrationCatalogEntry?> GetLatestCatalogEntryAsync(
        RegistrationIndex index,
        FeedCredential? credential,
        CancellationToken ct)
    {
        var lastPage = index.Items[^1];

        if (lastPage.Items is not null && lastPage.Items.Length > 0)
        {
            return lastPage.Items[^1].CatalogEntry;
        }

        if (lastPage.Id is not null)
        {
            var pageData = await this.FetchRegistrationPageAsync(lastPage.Id, credential, ct);

            if (pageData?.Items is not null && pageData.Items.Length > 0)
            {
                return pageData.Items[^1].CatalogEntry;
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches a single registration page by URL.
    /// </summary>
    /// <param name="pageUrl">The full URL to the page JSON.</param>
    /// <param name="credential">Optional credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized registration page.</returns>
    private async Task<RegistrationPage?> FetchRegistrationPageAsync(
        string pageUrl,
        FeedCredential? credential,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        FeedCredentialHelper.AddCredentialHeaders(request, credential);

        using var response = await this.HttpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);

        return JsonSerializer.Deserialize<RegistrationPage>(json, NuGetRegistrationClient.JsonOptions);
    }

    /// <summary>
    /// Constructs a Found <see cref="PackageMetadataResult"/> from a catalog entry.
    /// </summary>
    /// <param name="packageId">The requested package ID (used as fallback if entry ID is missing).</param>
    /// <param name="entry">The catalog entry to map.</param>
    /// <returns>A <see cref="PackageMetadataResult"/> with <see cref="RegistrationOutcome.Found"/>.</returns>
    private static PackageMetadataResult BuildFoundResult(
        string packageId,
        RegistrationCatalogEntry entry)
    {
        var data = new PackageRegistrationData(
            PackageId: entry.Id ?? packageId,
            Version: entry.Version ?? string.Empty,
            Authors: string.IsNullOrWhiteSpace(entry.Authors) ? null : entry.Authors,
            Description: string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description,
            ProjectUrl: string.IsNullOrWhiteSpace(entry.ProjectUrl) ? null : entry.ProjectUrl,
            LicenseExpression: string.IsNullOrWhiteSpace(entry.LicenseExpression) ? null : entry.LicenseExpression,
            LicenseUrl: string.IsNullOrWhiteSpace(entry.LicenseUrl) ? null : entry.LicenseUrl,
            Published: entry.Published,
            IsDeprecated: entry.Deprecation is JsonNode deprecation
                && deprecation.GetValueKind() != JsonValueKind.Null,
            HasVulnerabilities: entry.Vulnerabilities is not null && entry.Vulnerabilities.Length > 0,
            IsUnlisted: false);

        return new PackageMetadataResult(RegistrationOutcome.Found, data, null);
    }

    #endregion

    #region Private DTOs

    /// <summary>
    /// Top-level registration index returned by {baseUrl}/{packageId}/index.json.
    /// </summary>
    private sealed class RegistrationIndex
    {
        /// <summary>Gets or sets the registration pages for this package.</summary>
        [JsonPropertyName("items")]
        public RegistrationPage[] Items { get; set; } = [];
    }

    /// <summary>
    /// A single page in the registration index, covering a version range.
    /// Items may be inlined or null (requiring a separate page fetch).
    /// </summary>
    private sealed class RegistrationPage
    {
        /// <summary>Gets or sets the URL for this page (used to fetch non-inlined items).</summary>
        [JsonPropertyName("@id")]
        public string? Id { get; set; }

        /// <summary>Gets or sets the lowest version in this page's range.</summary>
        [JsonPropertyName("lower")]
        public string? Lower { get; set; }

        /// <summary>Gets or sets the highest version in this page's range.</summary>
        [JsonPropertyName("upper")]
        public string? Upper { get; set; }

        /// <summary>Gets or sets the inlined package items; null when the page must be fetched separately.</summary>
        [JsonPropertyName("items")]
        public RegistrationPageItem[]? Items { get; set; }
    }

    /// <summary>
    /// A single item within a registration page, wrapping a catalog entry.
    /// </summary>
    private sealed class RegistrationPageItem
    {
        /// <summary>Gets or sets the catalog entry containing package metadata.</summary>
        [JsonPropertyName("catalogEntry")]
        public RegistrationCatalogEntry? CatalogEntry { get; set; }
    }

    /// <summary>
    /// The catalog entry within a registration item, containing full package metadata
    /// including dependency groups used by the BFS engine.
    /// </summary>
    private sealed class RegistrationCatalogEntry
    {
        /// <summary>Gets or sets the package identifier.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>Gets or sets the package version string.</summary>
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        /// <summary>Gets or sets the package authors string.</summary>
        [JsonPropertyName("authors")]
        public string? Authors { get; set; }

        /// <summary>Gets or sets the package description.</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>Gets or sets the package project URL.</summary>
        [JsonPropertyName("projectUrl")]
        public string? ProjectUrl { get; set; }

        /// <summary>Gets or sets the SPDX license expression.</summary>
        [JsonPropertyName("licenseExpression")]
        public string? LicenseExpression { get; set; }

        /// <summary>Gets or sets the fallback license URL for packages predating license expressions.</summary>
        [JsonPropertyName("licenseUrl")]
        public string? LicenseUrl { get; set; }

        /// <summary>Gets or sets the publication date.</summary>
        [JsonPropertyName("published")]
        public DateTimeOffset? Published { get; set; }

        /// <summary>
        /// Gets or sets the deprecation node; a non-null, non-Null JSON node indicates the package is deprecated.
        /// </summary>
        [JsonPropertyName("deprecation")]
        public JsonNode? Deprecation { get; set; }

        /// <summary>Gets or sets the vulnerabilities array; non-empty indicates known vulnerabilities.</summary>
        [JsonPropertyName("vulnerabilities")]
        public JsonElement[]? Vulnerabilities { get; set; }

        /// <summary>Gets or sets the dependency groups for this package version, keyed by target framework.</summary>
        [JsonPropertyName("dependencyGroups")]
        public RegistrationDependencyGroup[]? DependencyGroups { get; set; }
    }

    /// <summary>
    /// A group of dependencies for a specific target framework within a catalog entry.
    /// </summary>
    private sealed class RegistrationDependencyGroup
    {
        /// <summary>Gets or sets the target framework moniker; null or empty for a catch-all group.</summary>
        [JsonPropertyName("targetFramework")]
        public string? TargetFramework { get; set; }

        /// <summary>Gets or sets the dependencies in this group.</summary>
        [JsonPropertyName("dependencies")]
        public RegistrationDependencyItem[]? Dependencies { get; set; }
    }

    /// <summary>
    /// A single dependency entry within a dependency group.
    /// </summary>
    private sealed class RegistrationDependencyItem
    {
        /// <summary>Gets or sets the NuGet package identifier of the dependency.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>Gets or sets the version range expression; null or empty means no constraint.</summary>
        [JsonPropertyName("range")]
        public string? Range { get; set; }
    }

    #endregion
}
