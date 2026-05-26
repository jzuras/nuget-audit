using NugetAudit.Core.Configuration;
using NugetAudit.Core.DependencyGraph;
using NugetAudit.Core.Models;
using NugetAudit.Core.Security;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Audit;

/// <summary>
/// Orchestrates a full NuGet package audit: dependency enumeration, metadata fetching,
/// trust evaluation, executable content detection, and security advisory checks.
/// </summary>
public class AuditRunner : IAuditRunner
{
    #region Properties

    /// <summary>
    /// Gets the client used to fetch package metadata from the NuGet Registration API.
    /// </summary>
    private INuGetRegistrationClient RegistrationClient { get; }

    /// <summary>
    /// Gets the client used to fetch verified status and owner names from the NuGet Search API.
    /// </summary>
    private INuGetSearchClient SearchClient { get; }

    /// <summary>
    /// Gets the evaluator that determines a package's trust status.
    /// </summary>
    private ITrustEvaluator TrustEvaluator { get; }

    /// <summary>
    /// Gets the loader for TrustConfig.json.
    /// </summary>
    private ITrustConfigLoader TrustConfigLoader { get; }

    /// <summary>
    /// Gets the service for PSM, lock file, and executable content checks.
    /// </summary>
    private ISecurityAdvisoryService SecurityAdvisoryService { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditRunner"/> class.
    /// </summary>
    /// <param name="registrationClient">NuGet Registration API client.</param>
    /// <param name="searchClient">NuGet Search API client.</param>
    /// <param name="trustEvaluator">Trust status evaluator.</param>
    /// <param name="trustConfigLoader">TrustConfig.json loader.</param>
    /// <param name="securityAdvisoryService">Security advisory checker.</param>
    public AuditRunner(
        INuGetRegistrationClient registrationClient,
        INuGetSearchClient searchClient,
        ITrustEvaluator trustEvaluator,
        ITrustConfigLoader trustConfigLoader,
        ISecurityAdvisoryService securityAdvisoryService)
    {
        this.RegistrationClient = registrationClient;
        this.SearchClient = searchClient;
        this.TrustEvaluator = trustEvaluator;
        this.TrustConfigLoader = trustConfigLoader;
        this.SecurityAdvisoryService = securityAdvisoryService;
    }

    /// <inheritdoc />
    public async Task<AuditResult> RunAuditAsync(
        AuditOptions options,
        Func<string, Task>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        await ReportProgressAsync(progress, $"Analyzing packages in: {options.Path}");

        // Pre-flight: if targeting a single .csproj, verify it has been restored.
        // Solutions are skipped — checking every project would require parsing the solution file.
        string resolvedPath = DotnetListPackageRunner.ResolveProjectPath(options.Path);

        if (resolvedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            string projectDir = Path.GetDirectoryName(resolvedPath) ?? resolvedPath;
            string assetsFile = Path.Combine(projectDir, "obj", "project.assets.json");

            if (!File.Exists(assetsFile))
            {
                throw new InvalidOperationException(
                    $"Project '{Path.GetFileName(resolvedPath)}' has not been restored. Run 'dotnet restore' first.");
            }
        }

        // Enumerate all packages via dotnet list package.
        await ReportProgressAsync(progress, "Running 'dotnet list package --include-transitive --format json'...");

        var (packageEntries, totalProjects) = await DotnetListPackageRunner.RunAsync(options.Path, ct);

        await ReportProgressAsync(
            progress,
            $"Found {packageEntries.Count} unique packages. Fetching metadata and verification status from NuGet API...");

        // Load trust configuration.
        string trustConfigPath = TrustConfigPathResolver.Resolve(options.Path, options.TrustConfigPath);
        var (trustConfig, hasTrustConfig) = this.TrustConfigLoader.LoadOrDefault(trustConfigPath);

        // Fetch NuGet cache path for exec content checks.
        string cachePath = NuGetCacheLocator.GetCachePath();

        // Fetch metadata and evaluate trust for each package.
        var packageInfoList = new List<PackageInfo>(packageEntries.Count);
        int current = 0;

        foreach (var entry in packageEntries)
        {
            current++;

            await ReportProgressAsync(
                progress,
                $"[{current}/{packageEntries.Count}] {entry.Id} {entry.Version}");

            var pkgInfo = await PackageInfoBuilder.BuildAsync(
                entry.Id,
                entry.Version,
                feedInfo: null,
                trustConfig,
                entry.DependencyType,
                cachePath,
                this.RegistrationClient,
                this.SearchClient,
                this.TrustEvaluator,
                this.SecurityAdvisoryService,
                ct);
            packageInfoList.Add(pkgInfo);
        }

        await ReportProgressAsync(
            progress,
            $"Successfully retrieved metadata for {packageInfoList.Count} packages.");

        // Security advisory checks.
        var psmStatus = this.SecurityAdvisoryService.CheckPackageSourceMapping(options.Path);
        var lockStatus = this.SecurityAdvisoryService.CheckLockFile(options.Path);

        return new AuditResult(
            [.. packageInfoList],
            psmStatus,
            lockStatus,
            totalProjects,
            hasTrustConfig,
            trustConfig.RecentDaysThreshold);
    }

    #region Private Helpers

    /// <summary>
    /// Invokes the progress callback if one was provided.
    /// </summary>
    /// <param name="progress">The optional progress callback.</param>
    /// <param name="message">The progress message.</param>
    private static async Task ReportProgressAsync(Func<string, Task>? progress, string message)
    {
        if (progress is not null)
        {
            await progress(message);
        }
    }

    #endregion
}
