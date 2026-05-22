using System.Net;
using System.Text;
using NugetAudit.Core.Models;
using NugetAudit.Core.NuGet;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="NuGetRegistrationClient"/>, covering all four RegistrationOutcome paths.
/// Uses a <see cref="FakeHttpMessageHandler"/> to avoid real network calls.
/// </summary>
public class NuGetRegistrationClientTests
{
    #region Helpers

    /// <summary>
    /// Creates a <see cref="NuGetRegistrationClient"/> backed by a fake HTTP handler.
    /// </summary>
    /// <param name="handler">A function that maps requests to responses.</param>
    /// <returns>A configured client instance.</returns>
    private static NuGetRegistrationClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var fakeHandler = new FakeHttpMessageHandler(handler);
        var httpClient = new HttpClient(fakeHandler);

        return new NuGetRegistrationClient(httpClient);
    }

    /// <summary>
    /// Creates an HTTP response with a JSON body and an optional status code.
    /// </summary>
    /// <param name="json">The JSON string to use as the response body.</param>
    /// <param name="statusCode">The HTTP status code; defaults to 200 OK.</param>
    /// <returns>An <see cref="HttpResponseMessage"/> with the given content.</returns>
    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    #endregion

    #region Found — Inlined Items

    /// <summary>
    /// When the requested version is found in inlined items, returns Found with correct metadata.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_VersionFoundInInlinedItems_ReturnsFound()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page",
                  "lower": "1.0.0",
                  "upper": "2.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "1.5.0",
                        "authors": "Test Author",
                        "description": "A test package"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var client = CreateClient(_ => JsonResponse(indexJson));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "1.5.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.Found, result.Outcome);
        Assert.NotNull(result.Data);
        Assert.Equal("TestPackage", result.Data.PackageId);
        Assert.Equal("1.5.0", result.Data.Version);
        Assert.Equal("Test Author", result.Data.Authors);
        Assert.False(result.Data.IsUnlisted);
    }

    /// <summary>
    /// Version matching is case-insensitive against the catalog entry version field.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_VersionMatchIsCaseInsensitive_ReturnsFound()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page",
                  "lower": "1.0.0",
                  "upper": "1.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "1.0.0"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var client = CreateClient(_ => JsonResponse(indexJson));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "1.0.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.Found, result.Outcome);
    }

    #endregion

    #region Found — Fetched Page

    /// <summary>
    /// When items are not inlined and the version falls within the page range,
    /// the page is fetched and the version is found within it.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_VersionInNonInlinedPage_FetchesPageAndReturnsFound()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page1",
                  "lower": "1.0.0",
                  "upper": "2.0.0"
                }
              ]
            }
            """;

        string pageJson = """
            {
              "items": [
                {
                  "catalogEntry": {
                    "id": "TestPackage",
                    "version": "1.5.0",
                    "authors": "Page Author"
                  }
                }
              ]
            }
            """;

        int callCount = 0;

        var client = CreateClient(request =>
        {
            callCount++;

            // First call: index; second call: page
            return callCount == 1
                ? JsonResponse(indexJson)
                : JsonResponse(pageJson);
        });

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "1.5.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.Found, result.Outcome);
        Assert.Equal(2, callCount);
        Assert.NotNull(result.Data);
        Assert.Equal("1.5.0", result.Data.Version);
    }

    #endregion

    #region Unlisted

    /// <summary>
    /// When the requested version falls within the index range but is not listed,
    /// returns Unlisted — the version was published and then delisted.
    /// The index covers 1.0.0–3.0.0; version 2.0.0 is absent but within that range.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_VersionWithinRangeButMissing_ReturnsUnlisted()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page",
                  "lower": "1.0.0",
                  "upper": "3.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "1.0.0"
                      }
                    },
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "3.0.0"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var client = CreateClient(_ => JsonResponse(indexJson));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "2.0.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.Unlisted, result.Outcome);
        Assert.NotNull(result.Data);
        Assert.Equal("2.0.0", result.Data.Version);
        Assert.True(result.Data.IsUnlisted);
        Assert.Null(result.ErrorMessage);
    }

    /// <summary>
    /// When the requested version is below the index lower bound, the version was never
    /// on this feed — returns PrivateFeed. This covers the case where a package ID later
    /// appears on nuget.org at a newer version while the project still uses an older
    /// version that was always only on a private feed.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_VersionBelowIndexLower_ReturnsPrivateFeed()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page",
                  "lower": "14.0.0",
                  "upper": "14.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "14.0.0"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var client = CreateClient(_ => JsonResponse(indexJson));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "13.0.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.PrivateFeed, result.Outcome);
    }

    /// <summary>
    /// When the requested version is above the index upper bound, the version was never
    /// on this feed — returns PrivateFeed. This covers pre-release or future versions
    /// that exist only on a private/CI feed.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_VersionAboveIndexUpper_ReturnsPrivateFeed()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page",
                  "lower": "1.0.0",
                  "upper": "1.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "1.0.0"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var client = CreateClient(_ => JsonResponse(indexJson));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "99.0.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.PrivateFeed, result.Outcome);
    }

    #endregion

    #region FallbackToLatest

    /// <summary>
    /// When the requested version is not found and fallbackToLatest is true,
    /// returns Found with the last version from the last page.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_VersionNotFoundFallbackTrue_ReturnsLatestVersion()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page",
                  "lower": "1.0.0",
                  "upper": "1.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "1.0.0"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var client = CreateClient(_ => JsonResponse(indexJson));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "99.0.0", true, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.Found, result.Outcome);
        Assert.NotNull(result.Data);
        Assert.Equal("1.0.0", result.Data.Version);
        Assert.False(result.Data.IsUnlisted);
    }

    #endregion

    #region PrivateFeed

    /// <summary>
    /// A 404 response maps to PrivateFeed outcome.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_NotFoundResponse_ReturnsPrivateFeed()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.GetPackageMetadataAsync(
            "PrivatePackage", "1.0.0", false, "https://private.example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.PrivateFeed, result.Outcome);
        Assert.Null(result.Data);
        Assert.Null(result.ErrorMessage);
    }

    #endregion

    #region Error

    /// <summary>
    /// A non-404 HTTP error returns Error outcome with a message.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_ServerError_ReturnsError()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "1.0.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.Error, result.Outcome);
        Assert.Null(result.Data);
        Assert.NotNull(result.ErrorMessage);
    }

    #endregion

    #region Metadata Mapping

    /// <summary>
    /// Deprecation field present maps to IsDeprecated = true.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_DeprecationPresent_IsDeprecatedTrue()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page",
                  "lower": "1.0.0",
                  "upper": "1.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "1.0.0",
                        "deprecation": { "message": "Use NewPackage instead" }
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var client = CreateClient(_ => JsonResponse(indexJson));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "1.0.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.Found, result.Outcome);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.IsDeprecated);
    }

    /// <summary>
    /// Vulnerabilities array present maps to HasVulnerabilities = true.
    /// </summary>
    [Fact]
    public async Task GetPackageMetadataAsync_VulnerabilitiesPresent_HasVulnerabilitiesTrue()
    {
        string indexJson = """
            {
              "items": [
                {
                  "@id": "https://example.com/page",
                  "lower": "1.0.0",
                  "upper": "1.0.0",
                  "items": [
                    {
                      "catalogEntry": {
                        "id": "TestPackage",
                        "version": "1.0.0",
                        "vulnerabilities": [
                          { "advisoryUrl": "https://github.com/advisories/GHSA-123" }
                        ]
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var client = CreateClient(_ => JsonResponse(indexJson));

        var result = await client.GetPackageMetadataAsync(
            "TestPackage", "1.0.0", false, "https://example.com/", null, CancellationToken.None);

        Assert.Equal(RegistrationOutcome.Found, result.Outcome);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.HasVulnerabilities);
    }

    #endregion
}
