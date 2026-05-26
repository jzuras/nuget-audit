using System.Net;
using System.Text;
using NugetAudit.Core.Trust;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PrivateFeedResolver"/>, covering feed discovery, PSM pattern
/// matching, and credential resolution from NuGet.Config. Uses a temp directory per test
/// to simulate the directory-hierarchy config walk, and a <see cref="FakeHttpMessageHandler"/>
/// for service-index and probe HTTP calls.
/// </summary>
public sealed class PrivateFeedResolverTests : IDisposable
{
    #region Setup / Teardown

    /// <summary>
    /// Gets the temporary directory used as the simulated solution directory per test.
    /// </summary>
    private string TempDir { get; }

    /// <summary>
    /// Initializes a new instance, creating a unique temp directory.
    /// </summary>
    public PrivateFeedResolverTests()
    {
        this.TempDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-pfr-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempDir);
    }

    /// <summary>
    /// Removes the temp directory and all its contents after each test.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(this.TempDir))
        {
            Directory.Delete(this.TempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #endregion

    #region Helpers

    private string SolutionFile => Path.Combine(this.TempDir, "MySolution.sln");

    private void WriteNuGetConfig(string content)
        => File.WriteAllText(Path.Combine(this.TempDir, "NuGet.Config"), content);

    private static PrivateFeedResolver MakeResolver(
        Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
    {
        var fake = new FakeHttpMessageHandler(
            handler ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        return new PrivateFeedResolver(new HttpClient(fake));
    }

    private static HttpResponseMessage ServiceIndexJson(string regUrl) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "resources": [
                    { "@id": "{{regUrl}}", "@type": "RegistrationsBaseUrl/3.6.0" }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        };

    #endregion

    #region No private feeds

    /// <summary>
    /// No NuGet.Config in the directory → returns null immediately.
    /// </summary>
    [Fact]
    public async Task ResolvePackageFeedAsync_NoConfigFile_ReturnsNull()
    {
        var resolver = MakeResolver();

        var result = await resolver.ResolvePackageFeedAsync(
            "SomePackage", this.SolutionFile, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// NuGet.Config that only lists nuget.org is not treated as a private feed → returns null.
    /// </summary>
    [Fact]
    public async Task ResolvePackageFeedAsync_OnlyNugetOrgSource_ReturnsNull()
    {
        this.WriteNuGetConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        var resolver = MakeResolver();

        var result = await resolver.ResolvePackageFeedAsync(
            "SomePackage", this.SolutionFile, CancellationToken.None);

        Assert.Null(result);
    }

    #endregion

    #region PSM matching

    /// <summary>
    /// PSM with an exact package ID match identifies the correct feed.
    /// The resolver returns a FeedInfo when the service index is reachable.
    /// </summary>
    [Fact]
    public async Task ResolvePackageFeedAsync_PsmExactMatch_ReturnsFeedInfo()
    {
        const string feedUrl = "https://myfeed.example.com/v3";
        const string regUrl = "https://myfeed.example.com/v3/registrations/";

        this.WriteNuGetConfig($"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="MyFeed" value="{feedUrl}/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="MyFeed">
                  <package pattern="MyCompany.Core" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var resolver = MakeResolver(_ => ServiceIndexJson(regUrl));

        var result = await resolver.ResolvePackageFeedAsync(
            "MyCompany.Core", this.SolutionFile, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(regUrl, result.RegistrationBaseUrl);
    }

    /// <summary>
    /// PSM with a wildcard pattern (MyCompany.*) matches a package with that prefix.
    /// </summary>
    [Fact]
    public async Task ResolvePackageFeedAsync_PsmWildcardMatch_ReturnsFeedInfo()
    {
        const string feedUrl = "https://myfeed.example.com/v3";
        const string regUrl = "https://myfeed.example.com/v3/registrations/";

        this.WriteNuGetConfig($"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="MyFeed" value="{feedUrl}/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="MyFeed">
                  <package pattern="MyCompany.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var resolver = MakeResolver(_ => ServiceIndexJson(regUrl));

        var result = await resolver.ResolvePackageFeedAsync(
            "MyCompany.Logging", this.SolutionFile, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(regUrl, result.RegistrationBaseUrl);
    }

    /// <summary>
    /// PSM wildcard does not match a package from a different namespace.
    /// </summary>
    [Fact]
    public async Task ResolvePackageFeedAsync_PsmWildcardNoMatch_ReturnsNull()
    {
        const string feedUrl = "https://myfeed.example.com/v3";

        this.WriteNuGetConfig($"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="MyFeed" value="{feedUrl}/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="MyFeed">
                  <package pattern="MyCompany.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        // Service index returns 404 so even if probed nothing matches.
        var resolver = MakeResolver(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await resolver.ResolvePackageFeedAsync(
            "OtherCompany.Widget", this.SolutionFile, CancellationToken.None);

        // PSM narrowed the feed list to MyFeed but service index was unreachable → null.
        Assert.Null(result);
    }

    #endregion

    #region Credential resolution (ClearTextPassword)

    /// <summary>
    /// Credentials from packageSourceCredentials (ClearTextPassword) are attached to the FeedInfo.
    /// </summary>
    [Fact]
    public async Task ResolvePackageFeedAsync_ClearTextCredentials_AttachedToFeedInfo()
    {
        const string feedUrl = "https://myfeed.example.com/v3";
        const string regUrl = "https://myfeed.example.com/v3/registrations/";

        this.WriteNuGetConfig($"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="MyFeed" value="{feedUrl}/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <MyFeed>
                  <add key="Username" value="myuser" />
                  <add key="ClearTextPassword" value="secret123" />
                </MyFeed>
              </packageSourceCredentials>
              <packageSourceMapping>
                <packageSource key="MyFeed">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var resolver = MakeResolver(_ => ServiceIndexJson(regUrl));

        var result = await resolver.ResolvePackageFeedAsync(
            "AnyPackage", this.SolutionFile, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Credential);
        Assert.Equal("secret123", result.Credential!.Password);
        Assert.Equal("myuser", result.Credential.Username);
    }

    #endregion
}
