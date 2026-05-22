using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NugetAudit.Core;
using System.Xml.Linq;
using NugetAudit.Core.Models;
using NugetAudit.Core.NuGet;
using NugetAudit.Core.Security;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Trust;

/// <summary>
/// Resolves the registration base URL and credentials for a package hosted on a private NuGet feed.
/// Walks NuGet.Config files from the solution directory up to the filesystem root and consults the
/// global NuGet.Config to discover configured private feeds. Uses Package Source Mapping when
/// available to identify the correct feed; otherwise probes each feed with a trial registration request.
/// </summary>
public class PrivateFeedResolver : IPrivateFeedResolver
{
    #region Properties

    /// <summary>
    /// Gets the HTTP client used for service index and package probe requests.
    /// </summary>
    private HttpClient HttpClient { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="PrivateFeedResolver"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client for querying feed service indexes and probing endpoints.</param>
    public PrivateFeedResolver(HttpClient httpClient)
    {
        this.HttpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<FeedInfo?> ResolvePackageFeedAsync(
        string packageId,
        string solutionPath,
        CancellationToken ct)
    {
        // Collect all private feeds from NuGet.Config files in the directory hierarchy.
        var feeds = CollectPrivateFeeds(solutionPath);

        if (feeds.Count == 0)
        {
            return null;
        }

        // Collect all NuGet.Config XMLs for credential lookup (project-level first).
        var configXmls = NuGetConfigWalker.WalkConfigPaths(NuGetConfigWalker.GetDirectory(solutionPath))
            .Select(p => { try { return XDocument.Load(p); } catch { return null; } })
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        // Determine which feed to try via Package Source Mapping (preferred) or all feeds.
        string? targetFeedName = FindFeedNameFromPsm(packageId, configXmls);

        var feedsToTry = targetFeedName is not null
            ? feeds.Where(f => string.Equals(f.Name, targetFeedName, StringComparison.OrdinalIgnoreCase)).ToList()
            : feeds;

        foreach (var feed in feedsToTry)
        {
            // Resolve credentials: try each NuGet.Config XML, then fall back to cred provider.
            FeedCredential? cred = ResolveCredentialsFromConfig(feed.Name, configXmls);

            if (cred is null)
            {
                cred = await ResolveCredentialsFromProviderAsync(feed.Url, ct);
            }

            string? regUrl = await GetFeedRegistrationUrlAsync(feed.Url, cred, ct);

            if (regUrl is null)
            {
                continue;
            }

            // If PSM identified this feed, trust it without probing.
            if (targetFeedName is not null)
            {
                return new FeedInfo(regUrl, cred);
            }

            // Trial-and-error: probe the registration endpoint for this specific package.
            bool found = await ProbePackageOnFeedAsync(regUrl, packageId, cred, ct);

            if (found is true)
            {
                return new FeedInfo(regUrl, cred);
            }
        }

        return null;
    }

    #region Feed Discovery

    /// <summary>
    /// Collects private NuGet feeds (non-nuget.org) from NuGet.Config files in the directory
    /// hierarchy from the solution path up to the filesystem root, and from the global config.
    /// The first definition of each feed name wins (project-level overrides global).
    /// </summary>
    /// <param name="solutionPath">Path to the solution file or directory.</param>
    /// <returns>Ordered list of discovered private feed definitions.</returns>
    private static List<FeedDefinition> CollectPrivateFeeds(string solutionPath)
    {
        string dir = NuGetConfigWalker.GetDirectory(solutionPath);
        var feeds = new List<FeedDefinition>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string configPath in NuGetConfigWalker.WalkConfigPaths(dir))
        {
            ExtractPrivateFeeds(configPath, feeds, seenNames);
        }

        return feeds;
    }

    /// <summary>
    /// Extracts non-nuget.org package sources from a NuGet.Config file and adds new ones to the list.
    /// </summary>
    /// <param name="configPath">Path to the NuGet.Config file.</param>
    /// <param name="feeds">Accumulator list for discovered feeds.</param>
    /// <param name="seenNames">Set of already-seen feed names (case-insensitive) to prevent duplicates.</param>
    private static void ExtractPrivateFeeds(
        string configPath,
        List<FeedDefinition> feeds,
        HashSet<string> seenNames)
    {
        try
        {
            var doc = XDocument.Load(configPath);
            var sources = doc.Root?.Element("packageSources");

            if (sources is null)
            {
                return;
            }

            foreach (var add in sources.Elements("add"))
            {
                string? key = add.Attribute("key")?.Value;
                string? val = add.Attribute("value")?.Value;

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val))
                {
                    continue;
                }

                // Skip nuget.org sources.
                if (val.Contains("nuget.org", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seenNames.Add(key))
                {
                    feeds.Add(new FeedDefinition(key, val, configPath));
                }
            }
        }
        catch { }
    }

    #endregion

    #region Package Source Mapping

    /// <summary>
    /// Searches loaded NuGet.Config documents for a packageSourceMapping entry matching the package ID.
    /// Returns the feed key name if found, or null if PSM is not configured or no match exists.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier to look up.</param>
    /// <param name="configXmls">Ordered config documents to search (project-level first).</param>
    /// <returns>The matching feed key name, or null if not found.</returns>
    private static string? FindFeedNameFromPsm(string packageId, List<XDocument> configXmls)
    {
        foreach (var doc in configXmls)
        {
            var psm = doc.Root?.Element("packageSourceMapping");

            if (psm is null)
            {
                continue;
            }

            foreach (var source in psm.Elements("packageSource"))
            {
                string? sourceKey = source.Attribute("key")?.Value;

                if (string.IsNullOrWhiteSpace(sourceKey))
                {
                    continue;
                }

                foreach (var pkg in source.Elements("package"))
                {
                    string? pattern = pkg.Attribute("pattern")?.Value;

                    if (string.IsNullOrWhiteSpace(pattern))
                    {
                        continue;
                    }

                    if (MatchesPattern(packageId, pattern))
                    {
                        return sourceKey;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Matches a package ID against a NuGet Package Source Mapping pattern.
    /// Patterns use * as a wildcard (e.g., "MyCompany.*" or "*").
    /// </summary>
    /// <param name="packageId">The package identifier to test.</param>
    /// <param name="pattern">The PSM pattern to match against.</param>
    /// <returns>True if the package ID matches the pattern.</returns>
    private static bool MatchesPattern(string packageId, string pattern)
    {
        if (pattern == "*")
        {
            return true;
        }

        if (!pattern.Contains('*'))
        {
            return string.Equals(packageId, pattern, StringComparison.OrdinalIgnoreCase);
        }

        // Convert glob pattern to regex: escape dots, replace * with .*
        string regexPattern = "^"
            + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*")
            + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            packageId,
            regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    #endregion

    #region Credential Resolution

    /// <summary>
    /// Tries to retrieve feed credentials from the loaded NuGet.Config XML documents.
    /// Attempts ClearTextPassword first, then DPAPI-encrypted Password (Windows only).
    /// Returns null if no credentials are found (caller may then try the credential provider).
    /// </summary>
    /// <param name="feedName">The feed name as it appears in NuGet.Config packageSources.</param>
    /// <param name="configXmls">Ordered config documents to search (project-level first).</param>
    /// <returns>Feed credentials, or null if not found in any config.</returns>
    private static FeedCredential? ResolveCredentialsFromConfig(
        string feedName,
        List<XDocument> configXmls)
    {
        // NuGet.Config uses the feed name with spaces replaced by underscores as the XML element name.
        string safeName = feedName.Replace(' ', '_');

        foreach (var doc in configXmls)
        {
            var credsNode = doc.Root?.Element("packageSourceCredentials");

            if (credsNode is null)
            {
                continue;
            }

            var feedCreds = credsNode.Element(safeName);

            if (feedCreds is null)
            {
                continue;
            }

            string? username = feedCreds.Elements("add")
                .FirstOrDefault(a => string.Equals(
                    a.Attribute("key")?.Value, "Username", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")?.Value;

            string? clearPwd = feedCreds.Elements("add")
                .FirstOrDefault(a => string.Equals(
                    a.Attribute("key")?.Value, "ClearTextPassword", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")?.Value;

            if (!string.IsNullOrWhiteSpace(clearPwd))
            {
                return new FeedCredential(username ?? string.Empty, clearPwd, FeedAuthScheme.Basic);
            }

            string? encPwd = feedCreds.Elements("add")
                .FirstOrDefault(a => string.Equals(
                    a.Attribute("key")?.Value, "Password", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")?.Value;

            if (!string.IsNullOrWhiteSpace(encPwd))
            {
                string? decrypted = TryDecryptDpapi(encPwd);

                if (decrypted is not null)
                {
                    return new FeedCredential(username ?? string.Empty, decrypted, FeedAuthScheme.Basic);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to decrypt a DPAPI-encrypted base64 password string.
    /// Only supported on Windows. Returns null on non-Windows platforms or on decryption failure.
    /// </summary>
    /// <param name="base64Encrypted">Base64-encoded DPAPI-encrypted ciphertext.</param>
    /// <returns>The decrypted password string, or null if decryption failed or is unsupported.</returns>
    private static string? TryDecryptDpapi(string base64Encrypted)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            byte[] encrypted = Convert.FromBase64String(base64Encrypted);
            byte[] decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                encrypted,
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to retrieve feed credentials via the Azure Artifacts Credential Provider.
    /// Probes for CredentialProvider.Microsoft.exe in standard locations and on PATH.
    /// Returns null if the provider is not found or fails.
    /// </summary>
    /// <param name="feedUrl">The feed URL to authenticate against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Bearer credentials if the provider succeeded, or null otherwise.</returns>
    private static async Task<FeedCredential?> ResolveCredentialsFromProviderAsync(
        string feedUrl,
        CancellationToken ct)
    {
        string? providerExe = FindCredentialProviderExe();

        if (providerExe is null)
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = providerExe,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add(feedUrl);
            psi.ArgumentList.Add("-V");
            psi.ArgumentList.Add("Verbose");

            using var process = Process.Start(psi);

            if (process is null)
            {
                return null;
            }

            string stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                return null;
            }

            string json = stdout.Trim();

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var output = JsonSerializer.Deserialize<CredentialProviderOutput>(
                json,
                CredentialProviderOutputJsonOptions);

            if (output is null || string.IsNullOrWhiteSpace(output.Password))
            {
                return null;
            }

            return new FeedCredential(
                output.Username ?? string.Empty,
                output.Password,
                FeedAuthScheme.Bearer);
        }
        catch
        {
            return null;
        }
    }

    private static JsonSerializerOptions CredentialProviderOutputJsonOptions => CoreJsonOptions.CaseInsensitive;

    /// <summary>
    /// Locates the Azure Artifacts Credential Provider executable using the standard probe order:
    /// 1. NUGET_CREDENTIALPROVIDERS_PATH environment variable
    /// 2. {userProfile}/.nuget/plugins/netfx/... (Windows only)
    /// 3. {userProfile}/.nuget/plugins/netcore/... (all platforms)
    /// 4. PATH lookup
    /// </summary>
    /// <returns>Full path to the credential provider executable, or null if not found.</returns>
    private static string? FindCredentialProviderExe()
    {
        string exeName = "CredentialProvider.Microsoft.exe";
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 1. NUGET_CREDENTIALPROVIDERS_PATH env var.
        string? envPath = Environment.GetEnvironmentVariable("NUGET_CREDENTIALPROVIDERS_PATH");

        if (!string.IsNullOrWhiteSpace(envPath))
        {
            string candidate = Path.Combine(envPath, exeName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 2. netfx path (Windows only).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string netfxPath = Path.Combine(
                userProfile,
                ".nuget", "plugins", "netfx", "CredentialProvider.Microsoft", exeName);

            if (File.Exists(netfxPath))
            {
                return netfxPath;
            }
        }

        // 3. netcore path (all platforms).
        string netcorePath = Path.Combine(
            userProfile,
            ".nuget", "plugins", "netcore", "CredentialProvider.Microsoft", exeName);

        if (File.Exists(netcorePath))
        {
            return netcorePath;
        }

        // 4. PATH lookup.
        string? fromPath = FindOnPath(exeName);
        return fromPath;
    }

    /// <summary>
    /// Searches PATH directories for an executable by name.
    /// </summary>
    /// <param name="exeName">The executable filename to find.</param>
    /// <returns>Full path to the executable, or null if not found.</returns>
    private static string? FindOnPath(string exeName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, exeName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    #endregion

    #region Feed Probing

    /// <summary>
    /// Queries a V3 NuGet feed's service index to find its RegistrationsBaseUrl.
    /// Prefers RegistrationsBaseUrl/3.6.0 over the generic RegistrationsBaseUrl resource type.
    /// Returns null for V2 feeds, unreachable feeds, or authentication failures.
    /// </summary>
    /// <param name="feedUrl">The feed URL (will have /index.json appended if not already present).</param>
    /// <param name="credential">Optional credentials for the feed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The registration base URL, or null on failure.</returns>
    private async Task<string?> GetFeedRegistrationUrlAsync(
        string feedUrl,
        FeedCredential? credential,
        CancellationToken ct)
    {
        try
        {
            string indexUrl = feedUrl.TrimEnd('/').EndsWith("index.json", StringComparison.OrdinalIgnoreCase)
                ? feedUrl
                : feedUrl.TrimEnd('/') + "/index.json";

            using var request = new HttpRequestMessage(HttpMethod.Get, indexUrl);
            FeedCredentialHelper.AddCredentialHeaders(request, credential);

            using var response = await this.HttpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(ct);
            var serviceIndex = JsonSerializer.Deserialize<ServiceIndex>(json, ServiceIndexJsonOptions);

            if (serviceIndex?.Resources is null)
            {
                return null;
            }

            // Prefer RegistrationsBaseUrl/3.6.0 for gzip+semver2 support.
            var regResource = serviceIndex.Resources
                .FirstOrDefault(r => string.Equals(
                    r.Type, "RegistrationsBaseUrl/3.6.0", StringComparison.OrdinalIgnoreCase));

            if (regResource is null)
            {
                regResource = serviceIndex.Resources
                    .FirstOrDefault(r => r.Type is not null
                        && r.Type.StartsWith("RegistrationsBaseUrl", StringComparison.OrdinalIgnoreCase));
            }

            return regResource?.Id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Probes the registration API endpoint on a feed to check whether a package is hosted there.
    /// Returns true if the endpoint responds successfully (2xx).
    /// </summary>
    /// <param name="registrationBaseUrl">The feed's registration base URL.</param>
    /// <param name="packageId">The NuGet package identifier to probe for.</param>
    /// <param name="credential">Optional credentials for the feed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the package was found on this feed.</returns>
    private async Task<bool> ProbePackageOnFeedAsync(
        string registrationBaseUrl,
        string packageId,
        FeedCredential? credential,
        CancellationToken ct)
    {
        try
        {
            string probeUrl =
                $"{registrationBaseUrl.TrimEnd('/')}/{packageId.ToLowerInvariant()}/index.json";

            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            FeedCredentialHelper.AddCredentialHeaders(request, credential);

            using var response = await this.HttpClient.SendAsync(request, ct);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static JsonSerializerOptions ServiceIndexJsonOptions => CoreJsonOptions.CaseInsensitive;

    #endregion

    #region Private DTOs

    /// <summary>
    /// A private feed discovered in a NuGet.Config file.
    /// </summary>
    /// <param name="Name">The feed name (key) from packageSources.</param>
    /// <param name="Url">The feed URL (value) from packageSources.</param>
    /// <param name="ConfigPath">The path to the NuGet.Config file that defines this feed.</param>
    private sealed record FeedDefinition(string Name, string Url, string ConfigPath);

    /// <summary>
    /// V3 NuGet feed service index response.
    /// </summary>
    private sealed class ServiceIndex
    {
        /// <summary>Gets or sets the list of service resources.</summary>
        [JsonPropertyName("resources")]
        public ServiceResource[]? Resources { get; set; }
    }

    /// <summary>
    /// A single resource entry in the V3 service index.
    /// </summary>
    private sealed class ServiceResource
    {
        /// <summary>Gets or sets the resource URL.</summary>
        [JsonPropertyName("@id")]
        public string? Id { get; set; }

        /// <summary>Gets or sets the resource type (e.g., "RegistrationsBaseUrl/3.6.0").</summary>
        [JsonPropertyName("@type")]
        public string? Type { get; set; }
    }

    /// <summary>
    /// Deserialization target for the JSON output from the Azure Artifacts Credential Provider.
    /// </summary>
    private sealed class CredentialProviderOutput
    {
        /// <summary>Gets or sets the username returned by the credential provider.</summary>
        [JsonPropertyName("Username")]
        public string? Username { get; set; }

        /// <summary>Gets or sets the password or bearer token returned by the credential provider.</summary>
        [JsonPropertyName("Password")]
        public string? Password { get; set; }
    }

    #endregion
}
