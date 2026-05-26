using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;
using NugetAudit.Core.Configuration;
using NugetAudit.Core.DependencyGraph;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Audit;

/// <summary>
/// Implements preview flows: <c>preview-update</c> (delta BFS from the current restore graph)
/// and <c>preview-restore</c> (full BFS from project file package references).
/// Both flows resolve the transitive dependency graph via the NuGet Registration API without
/// running dotnet restore, fetch trust metadata for added/changed packages, and return fully
/// evaluated results that the CLI renders to the terminal.
/// </summary>
public class PreviewService : IPreviewService
{
    #region Constants

    /// <summary>
    /// Gets the default target framework used for dependency resolution when none is specified.
    /// Matches the PS tool behavior. Documents a known limitation: transitive deps for
    /// TFM-specific packages may differ from the actual restore for other frameworks.
    /// </summary>
    private static string DefaultTargetFramework { get; } = "net10.0";

    /// <summary>
    /// Shared <see cref="HttpClient"/> used only for the nuget.org package-existence check in
    /// <c>IsOnNuGetOrgAsync</c>. Kept as a static singleton to avoid socket exhaustion.
    /// </summary>
    private static HttpClient NuGetOrgHttpClient { get; } = new() { Timeout = TimeSpan.FromSeconds(5) };

    #endregion

    #region Properties

    /// <summary>
    /// Gets the NuGet Registration API client used for version checks and BFS dependency fetching.
    /// </summary>
    private INuGetRegistrationClient RegistrationClient { get; }

    /// <summary>
    /// Gets the NuGet Search API client used for trust metadata (owner, verified flag).
    /// </summary>
    private INuGetSearchClient SearchClient { get; }

    /// <summary>
    /// Gets the trust evaluator.
    /// </summary>
    private ITrustEvaluator TrustEvaluator { get; }

    /// <summary>
    /// Gets the trust configuration loader.
    /// </summary>
    private ITrustConfigLoader TrustConfigLoader { get; }

    /// <summary>
    /// Gets the resolver that locates private feed registration URLs and credentials.
    /// </summary>
    private IPrivateFeedResolver PrivateFeedResolver { get; }

    /// <summary>
    /// Gets the version range resolver used during BFS to resolve transitive dependency ranges.
    /// </summary>
    private IVersionRangeResolver VersionRangeResolver { get; }

    /// <summary>
    /// Gets the project file parser used by preview-restore to seed the BFS from package references.
    /// </summary>
    private IProjectFileParser ProjectFileParser { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviewService"/> class.
    /// </summary>
    /// <param name="registrationClient">NuGet Registration API client.</param>
    /// <param name="searchClient">NuGet Search API client.</param>
    /// <param name="trustEvaluator">Trust status evaluator.</param>
    /// <param name="trustConfigLoader">TrustConfig.json loader.</param>
    /// <param name="privateFeedResolver">Private feed URL and credential resolver.</param>
    /// <param name="versionRangeResolver">Version range expression resolver.</param>
    /// <param name="projectFileParser">Project file PackageReference parser.</param>
    public PreviewService(
        INuGetRegistrationClient registrationClient,
        INuGetSearchClient searchClient,
        ITrustEvaluator trustEvaluator,
        ITrustConfigLoader trustConfigLoader,
        IPrivateFeedResolver privateFeedResolver,
        IVersionRangeResolver versionRangeResolver,
        IProjectFileParser projectFileParser)
    {
        this.RegistrationClient = registrationClient;
        this.SearchClient = searchClient;
        this.TrustEvaluator = trustEvaluator;
        this.TrustConfigLoader = trustConfigLoader;
        this.PrivateFeedResolver = privateFeedResolver;
        this.VersionRangeResolver = versionRangeResolver;
        this.ProjectFileParser = projectFileParser;
    }

    #region Public Interface

    /// <inheritdoc />
    public async Task<PreviewUpdateResult> PreviewUpdateAsync(
        PreviewUpdateOptions opts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var (trustConfig, hasTrustConfig) = this.TrustConfigLoader.LoadOrDefault(
            TrustConfigPathResolver.Resolve(opts.Path, opts.TrustConfigPath));

        // Build the current restore graph from dotnet list package.
        // If dotnet list package fails (e.g. lock file is stale after a package version bump),
        // fall back to reading packages.lock.json directly — which is the authoritative record
        // of the last successful restore and always present when RestoreLockedMode is in use.
        IReadOnlyList<PackageListEntry> packageEntries;

        try
        {
            (packageEntries, _) = await DotnetListPackageRunner.RunAsync(opts.Path, ct);
        }
        catch (InvalidOperationException)
        {
            packageEntries = ReadFromLockFiles(opts.Path);

            if (packageEntries.Count == 0)
            {
                throw;
            }
        }

        var currentGraph = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in packageEntries)
        {
            string key = entry.Id.ToLowerInvariant();

            if (!currentGraph.ContainsKey(key))
            {
                currentGraph[key] = new PackageEntry(entry.Id, entry.Version);
            }
        }

        string targetKey = opts.PackageId.ToLowerInvariant();
        bool isNewPackage = !currentGraph.ContainsKey(targetKey);

        // Version check: determine if the package is public or private, and resolve the
        // effective version (fallbackToLatest handles blank/invalid version).
        string versionToCheck = string.IsNullOrWhiteSpace(opts.NewVersion) ? string.Empty : opts.NewVersion;

        var versionCheck = await this.RegistrationClient.GetPackageMetadataAsync(
            opts.PackageId,
            versionToCheck,
            fallbackToLatest: true,
            PackageInfoBuilder.NuGetOrgRegistrationBaseUrl,
            credential: null,
            ct);

        if (versionCheck.Outcome == RegistrationOutcome.PrivateFeed)
        {
            // Private feed: version must be specified explicitly.
            if (string.IsNullOrWhiteSpace(opts.NewVersion))
            {
                return BuildPartialResult(
                    isNewPackage,
                    "VersionRequired");
            }

            // Resolve credentials and registration URL for this private package.
            FeedInfo? feedInfo = await this.PrivateFeedResolver.ResolvePackageFeedAsync(
                opts.PackageId, opts.Path, ct);

            if (feedInfo is null)
            {
                return BuildPartialResult(
                    isNewPackage,
                    "CredentialsUnavailable");
            }

            string resolvedVersion = opts.NewVersion!;

            // Private-feed packages always use BFS (exact restore via minimal csproj does not
            // support private feeds). Mark result as approximate.
            var (added, changed, removed, _) = await this.RunDeltaBfsAsync(
                opts.PackageId,
                resolvedVersion,
                currentGraph,
                feedInfo,
                PreviewService.DefaultTargetFramework,
                ct);

            // Transitive deps resolved by BFS always come from nuget.org — pass feedInfo: null
            // so their trust metadata is fetched from nuget.org, not the private feed.
            // Only the root package itself lives on the private feed; it is in the changed list,
            // not in added, so passing null here is correct for both lists.
            var addedInfos = await this.FetchTrustMetadataForNewPackagesAsync(
                added, feedInfo: null, trustConfig, ct);

            var changedInfos = await this.FetchTrustMetadataForChangedPackagesAsync(
                changed, feedInfo: null, trustConfig, ct);

            return new PreviewUpdateResult(
                Added: [.. addedInfos],
                Changed: [.. changedInfos],
                Removed: [.. removed],
                ResolvedVersion: resolvedVersion,
                VersionNote: null,
                IsNewPackage: isNewPackage,
                IsPartialResult: false,
                PartialResultReason: null,
                HasTrustConfig: hasTrustConfig,
                RecentDaysThreshold: trustConfig.RecentDaysThreshold,
                IsApproximate: true);
        }

        if (versionCheck.Outcome == RegistrationOutcome.Error || versionCheck.Data is null)
        {
            return BuildPartialResult(isNewPackage, "VersionRequired");
        }

        // Public package — compute resolved version and build version note.
        string publicResolvedVersion = versionCheck.Data.Version;
        string? versionNote = null;

        if (string.IsNullOrWhiteSpace(opts.NewVersion))
        {
            versionNote = $"No version specified; using latest available: {publicResolvedVersion}";
        }
        else if (!string.Equals(opts.NewVersion, publicResolvedVersion, StringComparison.OrdinalIgnoreCase))
        {
            versionNote = $"Version {opts.NewVersion} not found on nuget.org; using latest available: {publicResolvedVersion}";
        }

        // Detect private-to-public transition: the new version is on nuget.org, but was the
        // current installed version? If not, this package has moved from a private feed to nuget.org
        // — structurally identical to a supply chain attack. The trust status of the new version
        // (computed later) determines whether the warning is yellow (Verified) or red (Untrusted).
        bool isPrivateToPublicTransition = false;

        if (!isNewPackage && currentGraph.TryGetValue(targetKey, out var currentEntry))
        {
            var currentCheck = await this.RegistrationClient.GetPackageMetadataAsync(
                opts.PackageId,
                currentEntry.Version,
                fallbackToLatest: false,
                PackageInfoBuilder.NuGetOrgRegistrationBaseUrl,
                credential: null,
                ct);

            isPrivateToPublicTransition = currentCheck.Outcome == RegistrationOutcome.PrivateFeed;
        }

        List<PackageEntry> pubAdded;
        List<(PackageEntry New, string OldVersion)> pubChanged;
        List<string> pubRemoved;
        bool pubApproximate;

        if (opts.UseFast)
        {
            // Fast (BFS) mode: approximate resolution via NuGet Registration API.
            (pubAdded, pubChanged, pubRemoved, _) = await this.RunDeltaBfsAsync(
                opts.PackageId,
                publicResolvedVersion,
                currentGraph,
                feedInfo: null,
                PreviewService.DefaultTargetFramework,
                ct);

            // BFS is always approximate relative to exact dotnet restore resolution.
            pubApproximate = true;
        }
        else
        {
            // Exact mode (default): create a minimal temp project, run dotnet restore,
            // and compute the delta against the current graph.
            string tfm = this.ProjectFileParser.DetectTargetFramework(opts.Path)
                ?? PreviewService.DefaultTargetFramework;

            string? nugetConfigPath = FindNearestNuGetConfig(opts.Path);

            try
            {
                (pubAdded, pubChanged, pubRemoved, pubApproximate) = await RunExactDeltaAsync(
                    opts.PackageId,
                    publicResolvedVersion,
                    currentGraph,
                    tfm,
                    nugetConfigPath,
                    ct);
            }
            catch (InvalidOperationException ex) when (versionNote is not null)
            {
                // The tool fell back to a different version than the user requested (versionNote
                // is set). Carry the note in Exception.Data so the CLI can render it in yellow
                // before the red error — rather than prepending it into the red error message.
                var wrapped = new InvalidOperationException(ex.Message, ex);
                wrapped.Data["VersionNote"] = versionNote;
                throw wrapped;
            }
        }

        var pubAddedInfos = await this.FetchTrustMetadataForNewPackagesAsync(
            pubAdded, feedInfo: null, trustConfig, ct);

        var pubChangedInfos = await this.FetchTrustMetadataForChangedPackagesAsync(
            pubChanged, feedInfo: null, trustConfig, ct);

        return new PreviewUpdateResult(
            Added: [.. pubAddedInfos],
            Changed: [.. pubChangedInfos],
            Removed: [.. pubRemoved],
            ResolvedVersion: publicResolvedVersion,
            VersionNote: versionNote,
            IsNewPackage: isNewPackage,
            IsPartialResult: false,
            PartialResultReason: null,
            HasTrustConfig: hasTrustConfig,
            RecentDaysThreshold: trustConfig.RecentDaysThreshold,
            IsApproximate: pubApproximate,
            IsPrivateToPublicTransition: isPrivateToPublicTransition);
    }

    /// <inheritdoc />
    public async Task<PreviewRestoreResult> PreviewRestoreAsync(
        PreviewRestoreOptions opts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var (trustConfig, hasTrustConfig) = this.TrustConfigLoader.LoadOrDefault(
            TrustConfigPathResolver.Resolve(opts.Path, opts.TrustConfigPath));

        // Parse direct package references from the project file without restoring.
        // Collect non-fatal warnings for files that could not be parsed (multi-project solutions).
        var parseWarnings = new List<string>();
        var directRefs = this.ProjectFileParser.ParsePackageReferences(opts.Path, parseWarnings);

        // Build a set of direct-reference IDs so each resolved package can be labelled correctly.
        var directIds = new HashSet<string>(
            directRefs.Select(r => r.Id),
            StringComparer.OrdinalIgnoreCase);

        List<PackageEntry> allPackages;
        bool isApproximate;

        if (opts.UseFast)
        {
            // Fast (BFS) mode: resolve via NuGet Registration API without running dotnet restore.
            // Results are approximate — NuGet minimum-version selection on the real graph may differ.
            string targetFramework = opts.TargetFramework
                ?? this.ProjectFileParser.DetectTargetFramework(opts.Path)
                ?? PreviewService.DefaultTargetFramework;

            (allPackages, isApproximate) = await this.RunFullBfsAsync(directRefs, targetFramework, ct);

            // BFS is always approximate relative to exact dotnet restore resolution.
            isApproximate = true;
        }
        else
        {
            // Exact mode (default): create a synthetic csproj from the parsed direct refs,
            // run dotnet restore into a temp directory, read project.assets.json, then delete
            // everything. The real project is not touched.
            string targetFramework = opts.TargetFramework
                ?? this.ProjectFileParser.DetectTargetFramework(opts.Path)
                ?? PreviewService.DefaultTargetFramework;

            string? nugetConfigPath = FindNearestNuGetConfig(opts.Path);
            (allPackages, isApproximate) = await RunExactRestoreAsync(directRefs, targetFramework, nugetConfigPath, ct);
        }

        // Fetch trust metadata for all packages in the resolved graph.
        var packageInfos = new List<PackageInfo>(allPackages.Count);
        int privateFeedCount = 0;

        foreach (var entry in allPackages)
        {
            var depType = directIds.Contains(entry.Id)
                ? DependencyType.Direct
                : DependencyType.Transitive;

            var info = await PackageInfoBuilder.BuildAsync(
                entry.Id,
                entry.Version,
                feedInfo: null,
                trustConfig,
                depType,
                cachePath: null,
                this.RegistrationClient,
                this.SearchClient,
                this.TrustEvaluator,
                securityAdvisoryService: null,
                ct);

            if (info.TrustStatus == TrustStatus.PrivateFeed)
            {
                privateFeedCount++;
            }

            packageInfos.Add(info);
        }

        // Count packages needing review.
        int needsReviewCount = packageInfos.Count(p =>
            p.TrustStatus is TrustStatus.Untrusted
                or TrustStatus.VersionChanged
                or TrustStatus.VerifiedUnknownOwner);

        return new PreviewRestoreResult(
            Added: [.. packageInfos],
            DirectRefs: [.. directRefs],
            IsApproximate: isApproximate,
            PrivateFeedCount: privateFeedCount,
            NeedsReviewCount: needsReviewCount,
            HasTrustConfig: hasTrustConfig,
            RecentDaysThreshold: trustConfig.RecentDaysThreshold,
            ParseWarnings: parseWarnings.Count > 0 ? [.. parseWarnings] : null);
    }

    #endregion

    #region BFS Engine

    /// <summary>
    /// Runs a delta BFS starting from the current resolved graph to compute the dependency
    /// graph changes caused by adding or updating a single package.
    /// Working graph is keyed by lowercased package ID; fetched-set by "id|version" to prevent
    /// re-fetching the same (package, version) pair.
    /// </summary>
    /// <param name="packageId">The package being added or updated.</param>
    /// <param name="newVersion">The target version of the package.</param>
    /// <param name="currentGraph">The current resolved graph (key = lowercased ID).</param>
    /// <param name="feedInfo">Private feed info for the target package; null for public packages.</param>
    /// <param name="targetFramework">The TFM used for dependency group selection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Tuple of (Added entries, Changed entries with old versions, Removed IDs, IsApproximate flag).
    /// </returns>
    private async Task<(
        List<PackageEntry> Added,
        List<(PackageEntry New, string OldVersion)> Changed,
        List<string> Removed,
        bool IsApproximate)>
    RunDeltaBfsAsync(
        string packageId,
        string newVersion,
#pragma warning disable CA1859 // IReadOnlyDictionary is intentional — parameter must not mutate the caller's graph
        IReadOnlyDictionary<string, PackageEntry> currentGraph,
#pragma warning restore CA1859
        FeedInfo? feedInfo,
        string targetFramework,
        CancellationToken ct)
    {
        bool isApproximate = false;

        // Working graph starts as a copy of the current graph.
        var workingGraph = new Dictionary<string, PackageEntry>(
            currentGraph,
            StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<PackageEntry>();
        queue.Enqueue(new PackageEntry(packageId, newVersion));

        // Track (id|version) pairs already processed to avoid infinite loops.
        var fetched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            string fetchKey = $"{item.Id.ToLowerInvariant()}|{item.Version}";

            if (!fetched.Add(fetchKey))
            {
                continue;
            }

            // Determine which feed to query for this item's dependencies.
            // For the root package on a private feed, use feedInfo; for everything else, use nuget.org.
            string baseUrl;
            FeedCredential? credential;

            if (feedInfo is not null
                && string.Equals(item.Id, packageId, StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = feedInfo.RegistrationBaseUrl;
                credential = feedInfo.Credential;
            }
            else
            {
                baseUrl = PackageInfoBuilder.NuGetOrgRegistrationBaseUrl;
                credential = null;
            }

            var deps = await this.RegistrationClient.GetPackageDependenciesAsync(
                item.Id,
                item.Version,
                targetFramework,
                baseUrl,
                credential,
                ct);

            foreach (var dep in deps)
            {
                string depKey = dep.Id.ToLowerInvariant();

                // Skip packages already in the working graph — prune existing branches.
                if (workingGraph.ContainsKey(depKey))
                {
                    continue;
                }

                var resolved = await this.VersionRangeResolver.ResolveAsync(dep.Id, dep.Range, ct);

                if (resolved.IsApproximate is true)
                {
                    isApproximate = true;
                }

                workingGraph[depKey] = new PackageEntry(dep.Id, resolved.Version);
                queue.Enqueue(new PackageEntry(dep.Id, resolved.Version));
            }
        }

        // Ensure the target package itself is recorded at the new version.
        string targetKeyLower = packageId.ToLowerInvariant();
        workingGraph[targetKeyLower] = new PackageEntry(packageId, newVersion);

        // Compute diff relative to the current graph.
        var added = new List<PackageEntry>();
        var changed = new List<(PackageEntry New, string OldVersion)>();

        foreach (var kv in workingGraph)
        {
            if (currentGraph.TryGetValue(kv.Key, out var existing))
            {
                if (!string.Equals(existing.Version, kv.Value.Version, StringComparison.OrdinalIgnoreCase))
                {
                    changed.Add((kv.Value, existing.Version));
                }
            }
            else
            {
                added.Add(kv.Value);
            }
        }

        // Removed detection is not implemented (requires full re-resolution from scratch).
        return (added, changed, Removed: [], isApproximate);
    }

    /// <summary>
    /// Runs a full BFS from an empty graph seeded by the direct package references.
    /// Resolves the complete transitive dependency graph as it would appear after a fresh restore.
    /// </summary>
    /// <param name="seeds">Direct package references to seed the BFS with.</param>
    /// <param name="targetFramework">The TFM used for dependency group selection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Tuple of (all resolved packages sorted by ID, IsApproximate flag).
    /// </returns>
    private async Task<(List<PackageEntry> Packages, bool IsApproximate)> RunFullBfsAsync(
        IReadOnlyList<PackageRef> seeds,
        string targetFramework,
        CancellationToken ct)
    {
        bool isApproximate = false;
        var workingGraph = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<PackageEntry>();
        var fetched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Seed the graph and queue with all direct references.
        foreach (var seed in seeds)
        {
            string key = seed.Id.ToLowerInvariant();

            // A bare version string (e.g. "10.0.6") is a pin — use it exactly without
            // calling the resolver. This matches dotnet restore, which treats a bare
            // Version="X.Y.Z" in a csproj as an exact constraint for the direct reference.
            // Range expressions (CPM wildcards, "[1.0, 2.0)", etc.) go through the resolver.
            string resolvedVersion;

            if (NuGetVersion.TryParse(seed.Version.Trim(), out var exactVersion))
            {
                resolvedVersion = exactVersion.ToNormalizedString();
            }
            else
            {
                var resolved = await this.VersionRangeResolver.ResolveAsync(seed.Id, seed.Version, ct);

                if (resolved.IsApproximate is true)
                {
                    isApproximate = true;
                }

                resolvedVersion = resolved.Version;
            }

            workingGraph[key] = new PackageEntry(seed.Id, resolvedVersion);
            queue.Enqueue(new PackageEntry(seed.Id, resolvedVersion));
        }

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            string fetchKey = $"{item.Id.ToLowerInvariant()}|{item.Version}";

            if (!fetched.Add(fetchKey))
            {
                continue;
            }

            var deps = await this.RegistrationClient.GetPackageDependenciesAsync(
                item.Id,
                item.Version,
                targetFramework,
                PackageInfoBuilder.NuGetOrgRegistrationBaseUrl,
                credential: null,
                ct);

            foreach (var dep in deps)
            {
                string depKey = dep.Id.ToLowerInvariant();

                if (workingGraph.ContainsKey(depKey))
                {
                    continue;
                }

                var resolved = await this.VersionRangeResolver.ResolveAsync(dep.Id, dep.Range, ct);

                if (resolved.IsApproximate is true)
                {
                    isApproximate = true;
                }

                workingGraph[depKey] = new PackageEntry(dep.Id, resolved.Version);
                queue.Enqueue(new PackageEntry(dep.Id, resolved.Version));
            }
        }

        var sortedPackages = workingGraph.Values
            .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (sortedPackages, isApproximate);
    }

    #endregion

    #region Exact Restore Engine

    /// <summary>
    /// Exact-restore path for <c>preview-restore</c>: creates a synthetic <c>.csproj</c>
    /// in a temp directory containing all direct package references, runs <c>dotnet restore</c>
    /// with the NuGet global packages cache redirected to a second temp directory, reads the
    /// resulting <c>project.assets.json</c>, then deletes both temp directories.
    /// The real project is not modified in any way.
    /// </summary>
    /// <param name="directRefs">Direct package references parsed from the project file.</param>
    /// <param name="targetFramework">TFM for the synthetic project (auto-detected or specified by the user).</param>
    /// <param name="nugetConfigPath">Optional path to a NuGet.Config file passed to <c>dotnet restore</c> via <c>--configfile</c>. Null uses the default config resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (resolved packages, IsApproximate = false).</returns>
    private static async Task<(List<PackageEntry> Packages, bool IsApproximate)> RunExactRestoreAsync(
        IReadOnlyList<PackageRef> directRefs,
        string targetFramework,
        string? nugetConfigPath,
        CancellationToken ct)
    {
        string uniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string tempProjectDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-{uniqueId}");
        string tempPkgDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-pkg-{uniqueId}");

        Directory.CreateDirectory(tempProjectDir);
        Directory.CreateDirectory(tempPkgDir);

        try
        {
            string csprojPath = Path.Combine(tempProjectDir, "preview.csproj");
            await File.WriteAllTextAsync(csprojPath, BuildMinimalCsproj(directRefs, targetFramework), ct);

            // Directory.Build.props stops MSBuild from walking up to parent build files
            // (which may include Directory.Packages.props that enforces CPM).
            string buildPropsPath = Path.Combine(tempProjectDir, "Directory.Build.props");
            await File.WriteAllTextAsync(buildPropsPath, PreviewService.IsolatedBuildProps, ct);

            await ExecuteDotnetRestoreAsync(csprojPath, tempPkgDir, nugetConfigPath, ct);

            string assetsFile = Path.Combine(tempProjectDir, "obj", "project.assets.json");
            var packages = File.Exists(assetsFile)
                ? ReadPackagesFromAssetsFile(assetsFile).ToList()
                : [];

            return (packages, IsApproximate: false);
        }
        finally
        {
            TryDeleteDirectory(tempProjectDir);
            TryDeleteDirectory(tempPkgDir);
        }
    }

    /// <summary>
    /// Exact-restore path for <c>preview-update</c>: creates a minimal temp project referencing
    /// the target package at the new version, runs <c>dotnet restore</c> into a temp packages
    /// directory, reads <c>project.assets.json</c> to obtain the new graph, then deletes both
    /// temp directories. Computes the delta against <paramref name="currentGraph"/>.
    /// </summary>
    /// <param name="packageId">The package being added or updated.</param>
    /// <param name="newVersion">The target version.</param>
    /// <param name="currentGraph">The current resolved graph (key = lowercased ID).</param>
    /// <param name="targetFramework">TFM for the temp project.</param>
    /// <param name="nugetConfigPath">Optional path to a NuGet.Config file passed to <c>dotnet restore</c> via <c>--configfile</c>. Null uses the default config resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (Added, Changed, Removed, IsApproximate = false).</returns>
    private static async Task<(
        List<PackageEntry> Added,
        List<(PackageEntry New, string OldVersion)> Changed,
        List<string> Removed,
        bool IsApproximate)>
    RunExactDeltaAsync(
        string packageId,
        string newVersion,
#pragma warning disable CA1859 // IReadOnlyDictionary is intentional — parameter must not mutate the caller's graph
        IReadOnlyDictionary<string, PackageEntry> currentGraph,
#pragma warning restore CA1859
        string targetFramework,
        string? nugetConfigPath,
        CancellationToken ct)
    {
        string uniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string tempProjectDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-{uniqueId}");
        string tempPkgDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-pkg-{uniqueId}");

        Directory.CreateDirectory(tempProjectDir);
        Directory.CreateDirectory(tempPkgDir);

        try
        {
            string csprojPath = Path.Combine(tempProjectDir, "preview.csproj");
            await File.WriteAllTextAsync(csprojPath, BuildMinimalCsproj(packageId, newVersion, targetFramework), ct);

            // Directory.Build.props prevents MSBuild from walking up to parent build files,
            // which may include Directory.Packages.props (CPM) that conflicts with our explicit version.
            string buildPropsPath = Path.Combine(tempProjectDir, "Directory.Build.props");
            await File.WriteAllTextAsync(buildPropsPath, PreviewService.IsolatedBuildProps, ct);

            await ExecuteDotnetRestoreAsync(csprojPath, tempPkgDir, nugetConfigPath, ct);

            string assetsFile = Path.Combine(tempProjectDir, "obj", "project.assets.json");
            var newGraphPackages = File.Exists(assetsFile)
                ? ReadPackagesFromAssetsFile(assetsFile)
                : (IReadOnlyList<PackageEntry>)[];

            // Build new-graph dictionary — target package itself is the root.
            var newGraph = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var pkg in newGraphPackages)
            {
                newGraph[pkg.Id.ToLowerInvariant()] = pkg;
            }

            // Ensure the target package is recorded even if not in libraries (unlikely).
            string targetKeyLower = packageId.ToLowerInvariant();

            if (!newGraph.ContainsKey(targetKeyLower))
            {
                newGraph[targetKeyLower] = new PackageEntry(packageId, newVersion);
            }

            // Compute diff relative to the current solution graph.
            var added = new List<PackageEntry>();
            var changed = new List<(PackageEntry New, string OldVersion)>();

            foreach (var kv in newGraph)
            {
                if (currentGraph.TryGetValue(kv.Key, out var existing))
                {
                    if (!string.Equals(existing.Version, kv.Value.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        changed.Add((kv.Value, existing.Version));
                    }
                }
                else
                {
                    added.Add(kv.Value);
                }
            }

            // Removed detection is not implemented (matches BFS behaviour).
            return (added, changed, Removed: [], IsApproximate: false);
        }
        finally
        {
            TryDeleteDirectory(tempProjectDir);
            TryDeleteDirectory(tempPkgDir);
        }
    }

    /// <summary>
    /// Shells out to <c>dotnet restore</c> with the NuGet global packages cache redirected
    /// to <paramref name="packagesTempDir"/>. Throws <see cref="InvalidOperationException"/>
    /// on non-zero exit code.
    /// </summary>
    /// <param name="path">Path to the solution, project, or directory to restore.</param>
    /// <param name="packagesTempDir">Temp directory that receives downloaded package files.</param>
    /// <param name="nugetConfigPath">Optional path to a NuGet.Config file passed via <c>--configfile</c>. Null uses the default config resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task ExecuteDotnetRestoreAsync(
        string path,
        string packagesTempDir,
        string? nugetConfigPath,
        CancellationToken ct)
    {
        string resolvedPath = DotnetListPackageRunner.ResolveProjectPath(path);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(resolvedPath);
        psi.ArgumentList.Add("--packages");
        psi.ArgumentList.Add(packagesTempDir);

        if (nugetConfigPath is not null)
        {
            psi.ArgumentList.Add("--configfile");
            psi.ArgumentList.Add(nugetConfigPath);
        }

        // Belt-and-suspenders: env var overrides the global packages cache for this process.
        psi.Environment["NUGET_PACKAGES"] = packagesTempDir;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet restore process.");

        // Read stdout and stderr concurrently to prevent buffer-full deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            string detail = stderr.Trim().Length > 0
                ? stderr.Trim()
                : stdout.Trim();

            // NU1101: package not found on any configured source — common when a private feed
            // is missing from NuGet.config or credentials are absent. Rewrite to a clean message
            // that does not expose the internal temp project path.
            if (detail.Contains("NU1101", StringComparison.Ordinal))
            {
                var missing = System.Text.RegularExpressions.Regex
                    .Matches(detail, @"NU1101: Unable to find package (.+?)\. No packages")
                    .Select(m => m.Groups[1].Value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string packageList = missing.Count > 0
                    ? string.Join(", ", missing.Select(p => $"'{p}'"))
                    : "one or more packages";

                throw new InvalidOperationException(
                    $"Cannot resolve {packageList} on any configured NuGet source. " +
                    "If this is a private feed package, ensure NuGet.config includes " +
                    "the feed URL and credentials are configured.");
            }

            // NU1102 + "were not considered": Package Source Mapping (PSM) restricted the search
            // to a private feed and excluded nuget.org. This happens when a vendor that previously
            // distributed only via a private feed publishes to nuget.org at a newer version.
            // Check whether the package is now available on nuget.org and produce an actionable message.
            if (detail.Contains("NU1102", StringComparison.Ordinal)
                && detail.Contains("were not considered", StringComparison.Ordinal))
            {
                var idMatch = System.Text.RegularExpressions.Regex
                    .Match(detail, @"NU1102: Unable to find package (.+?) with version");
                string pkgId = idMatch.Success ? idMatch.Groups[1].Value.Trim() : string.Empty;

                // Extract the minimum version from "(>= X.Y.Z)" — this is the exact version
                // requested; NuGet displays exact PackageReference versions as ">= X.Y.Z".
                var versionMatch = System.Text.RegularExpressions.Regex
                    .Match(detail, @"with version \(>= (.+?)\)");
                string targetVersion = versionMatch.Success ? versionMatch.Groups[1].Value.Trim() : string.Empty;

                var restrictedSources = System.Text.RegularExpressions.Regex
                    .Matches(detail, @"Found \d+ version\(s\) in (.+?) \[")
                    .Select(m => m.Groups[1].Value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string sourceList = restrictedSources.Count > 0
                    ? string.Join(", ", restrictedSources.Select(s => $"'{s}'"))
                    : "the configured private source";

                // Only show the PSM message if the specific requested version actually exists on
                // nuget.org — otherwise the version genuinely doesn't exist anywhere and the
                // generic restore error is more accurate.
                if (!string.IsNullOrEmpty(pkgId)
                    && !string.IsNullOrEmpty(targetVersion)
                    && await IsVersionOnNuGetOrgAsync(pkgId, targetVersion, ct))
                {
                    throw new InvalidOperationException(
                        $"'{pkgId}' v{targetVersion} is available on nuget.org but Package Source " +
                        $"Mapping is restricting the search to {sourceList}. " +
                        $"To preview this update, add a nuget.org mapping for '{pkgId}' in your " +
                        "NuGet.config <packageSourceMapping> section.");
                }
            }

            throw new InvalidOperationException(
                $"dotnet restore failed (exit code {process.ExitCode}): {detail}");
        }
    }

    /// <summary>
    /// Parses the <c>libraries</c> section of a <c>project.assets.json</c> file and returns
    /// all NuGet package entries (type == "package"). Project references are skipped.
    /// </summary>
    /// <param name="assetsFilePath">Absolute path to a <c>project.assets.json</c> file.</param>
    /// <returns>List of <see cref="PackageEntry"/> records parsed from the file.</returns>
    private static List<PackageEntry> ReadPackagesFromAssetsFile(string assetsFilePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsFilePath));

        if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return [];
        }

        var result = new List<PackageEntry>();

        foreach (var lib in libraries.EnumerateObject())
        {
            // Skip project references — only NuGet packages matter.
            if (!lib.Value.TryGetProperty("type", out var typeEl)
                || !string.Equals(typeEl.GetString(), "package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Library keys are in "PackageId/Version" format.
            // Package IDs and versions cannot contain '/', so LastIndexOf is safe.
            int slash = lib.Name.LastIndexOf('/');

            if (slash < 0)
            {
                continue;
            }

            string id = lib.Name[..slash];
            string version = lib.Name[(slash + 1)..];

            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version))
            {
                result.Add(new PackageEntry(id, version));
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a minimal <c>.csproj</c> content string referencing a single NuGet package.
    /// Used by <see cref="RunExactDeltaAsync"/> to create the temp project for exact restore.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The concrete version string.</param>
    /// <param name="targetFramework">The target framework moniker (e.g. "net10.0").</param>
    /// <returns>The csproj XML content as a string.</returns>
    private static string BuildMinimalCsproj(string packageId, string version, string targetFramework) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>{targetFramework}</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="{packageId}" Version="{version}" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>
    /// Builds a minimal <c>.csproj</c> content string referencing multiple NuGet packages.
    /// Used by <see cref="RunExactRestoreAsync"/> to create the temp project for exact restore.
    /// </summary>
    /// <param name="packageRefs">The direct package references to include.</param>
    /// <param name="targetFramework">The target framework moniker (e.g. "net10.0").</param>
    /// <returns>The csproj XML content as a string.</returns>
    private static string BuildMinimalCsproj(IReadOnlyList<PackageRef> packageRefs, string targetFramework)
    {
        var items = string.Join(
            "\n    ",
            packageRefs.Select(r => $"<PackageReference Include=\"{r.Id}\" Version=\"{r.Version}\" />"));

        return $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>{targetFramework}</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            {items}
          </ItemGroup>
        </Project>
        """;
    }

    /// <summary>
    /// Gets the content for a <c>Directory.Build.props</c> file placed in the temp project
    /// directory. Its presence stops MSBuild from walking up the directory tree to discover
    /// parent <c>Directory.Build.props</c> files, which may include <c>Directory.Packages.props</c>
    /// (CPM) that conflicts with the explicit version specified in the minimal csproj, or a
    /// <c>RestoreLockedMode=true</c> setting that would fail restore without a lock file.
    /// </summary>
    private static string IsolatedBuildProps { get; } =
        """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
            <RestoreLockedMode>false</RestoreLockedMode>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// Deletes a directory and all its contents, suppressing all exceptions.
    /// Used for best-effort cleanup of temp directories.
    /// </summary>
    /// <param name="path">Directory path to delete.</param>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup — temp directory deletion failures are non-fatal.
        }
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="packageId"/> at exactly <paramref name="version"/>
    /// is listed on nuget.org. Fetches the flat-container version index and checks for a
    /// case-insensitive match. Returns <c>false</c> on any network error or non-success response.
    /// </summary>
    /// <param name="packageId">Case-insensitive NuGet package ID to check.</param>
    /// <param name="version">Exact version string to look for (e.g. "14.0.0").</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<bool> IsVersionOnNuGetOrgAsync(
        string packageId, string version, CancellationToken ct)
    {
        try
        {
            var url = new Uri($"https://api.nuget.org/v3-flatcontainer/" +
                              $"{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json");
            using var response = await NuGetOrgHttpClient.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("versions", out var versions))
            {
                return false;
            }

            return versions.EnumerateArray()
                .Any(v => string.Equals(v.GetString(), version, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks up the directory hierarchy from <paramref name="projectPath"/> and returns the path
    /// to the first <c>NuGet.Config</c> file found (case-insensitive), or <c>null</c> if none exists.
    /// Passed via <c>--configfile</c> to the temp-project <c>dotnet restore</c> so that private
    /// feed sources and credentials defined near the original project are honoured.
    /// </summary>
    /// <param name="projectPath">Path to the project file or directory.</param>
    /// <returns>Absolute path to the nearest NuGet.Config, or <c>null</c>.</returns>
    private static string? FindNearestNuGetConfig(string projectPath)
    {
        string? dir = File.Exists(projectPath)
            ? Path.GetDirectoryName(projectPath)
            : Directory.Exists(projectPath) ? projectPath : null;

        while (!string.IsNullOrEmpty(dir))
        {
            var matches = Directory.GetFiles(
                dir,
                "NuGet.Config",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });

            if (matches.Length > 0)
            {
                return matches[0];
            }

            string? parent = Path.GetDirectoryName(dir);

            if (parent == dir)
            {
                break;
            }

            dir = parent;
        }

        return null;
    }

    #endregion

    #region Trust Metadata

    /// <summary>
    /// Fetches trust metadata for a list of newly added packages and returns a <see cref="PackageInfo"/>
    /// for each, evaluated against the provided trust configuration.
    /// </summary>
    /// <param name="added">Packages added to the dependency graph.</param>
    /// <param name="feedInfo">Private feed info for packages on a private feed; null for public.</param>
    /// <param name="trustConfig">The loaded trust configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of evaluated package info records in the same order as <paramref name="added"/>.</returns>
    private async Task<List<PackageInfo>> FetchTrustMetadataForNewPackagesAsync(
        List<PackageEntry> added,
        FeedInfo? feedInfo,
        TrustConfig trustConfig,
        CancellationToken ct)
    {
        var result = new List<PackageInfo>(added.Count);

        foreach (var entry in added)
        {
            var info = await PackageInfoBuilder.BuildAsync(
                entry.Id,
                entry.Version,
                feedInfo,
                trustConfig,
                DependencyType.Transitive,
                cachePath: null,
                this.RegistrationClient,
                this.SearchClient,
                this.TrustEvaluator,
                securityAdvisoryService: null,
                ct);

            result.Add(info);
        }

        return result;
    }

    /// <summary>
    /// Fetches trust metadata for a list of changed packages and returns <see cref="PackageChangedEntry"/>
    /// records, each carrying the new <see cref="PackageInfo"/> and the old version string.
    /// </summary>
    /// <param name="changed">Changed package entries with new version and old version.</param>
    /// <param name="feedInfo">Private feed info; null for public packages.</param>
    /// <param name="trustConfig">The loaded trust configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of evaluated changed entries.</returns>
    private async Task<List<PackageChangedEntry>> FetchTrustMetadataForChangedPackagesAsync(
        List<(PackageEntry New, string OldVersion)> changed,
        FeedInfo? feedInfo,
        TrustConfig trustConfig,
        CancellationToken ct)
    {
        var result = new List<PackageChangedEntry>(changed.Count);

        foreach (var (newEntry, oldVersion) in changed)
        {
            var info = await PackageInfoBuilder.BuildAsync(
                newEntry.Id,
                newEntry.Version,
                feedInfo,
                trustConfig,
                DependencyType.Transitive,
                cachePath: null,
                this.RegistrationClient,
                this.SearchClient,
                this.TrustEvaluator,
                securityAdvisoryService: null,
                ct);

            result.Add(new PackageChangedEntry(info, oldVersion));
        }

        return result;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Reads the current resolved graph from <c>packages.lock.json</c> files found under
    /// <paramref name="path"/>. Used as a fallback when <c>dotnet list package</c> fails
    /// because the lock file is stale (e.g. after a package version bump with RestoreLockedMode=true).
    /// Direct and CentralTransitive entries take precedence over Transitive for the same package ID.
    /// Project references are skipped.
    /// </summary>
    /// <param name="path">The path option value — a .csproj, .sln, .slnx, or directory.</param>
    /// <returns>Deduplicated list of package entries, or empty if no lock files were found.</returns>
    private static List<PackageListEntry> ReadFromLockFiles(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string searchDir = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? fullPath;

        // Collect all packages: Direct wins over Transitive for the same ID.
        var direct = new Dictionary<string, PackageListEntry>(StringComparer.OrdinalIgnoreCase);
        var transitive = new Dictionary<string, PackageListEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (string lockFile in Directory.EnumerateFiles(
            searchDir, "packages.lock.json", SearchOption.AllDirectories))
        {
            try
            {
                string json = File.ReadAllText(lockFile);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("dependencies", out var deps))
                {
                    continue;
                }

                // Merge all TFMs — if a package appears in any TFM as Direct it wins globally.
                foreach (var tfm in deps.EnumerateObject())
                {
                    foreach (var pkg in tfm.Value.EnumerateObject())
                    {
                        string id = pkg.Name;

                        if (!pkg.Value.TryGetProperty("type", out var typeEl)
                            || !pkg.Value.TryGetProperty("resolved", out var resolvedEl))
                        {
                            continue;
                        }

                        string type = typeEl.GetString() ?? string.Empty;
                        string version = resolvedEl.GetString() ?? string.Empty;

                        if (string.IsNullOrWhiteSpace(version))
                        {
                            continue;
                        }

                        // Skip project references — only NuGet packages matter.
                        if (string.Equals(type, "Project", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        bool isDirect = string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(type, "CentralTransitive", StringComparison.OrdinalIgnoreCase);

                        string key = id.ToLowerInvariant();

                        if (isDirect)
                        {
                            direct[key] = new PackageListEntry(id, version, DependencyType.Direct);
                        }
                        else if (!direct.ContainsKey(key))
                        {
                            transitive[key] = new PackageListEntry(id, version, DependencyType.Transitive);
                        }
                    }
                }
            }
            catch
            {
                // Malformed or unreadable lock file entry — skip it and continue with remaining projects.
            }
        }

        // Merge: direct entries win; add transitives only where no direct entry exists.
        var result = new List<PackageListEntry>(direct.Values);

        foreach (var (key, entry) in transitive)
        {
            if (!direct.ContainsKey(key))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a partial <see cref="PreviewUpdateResult"/> for early-return paths where the full
    /// graph cannot be resolved (private feed with no version specified, or no credentials).
    /// </summary>
    /// <param name="isNewPackage">True if the package is being added; false if being updated.</param>
    /// <param name="reason">The partial result reason: "VersionRequired" or "CredentialsUnavailable".</param>
    /// <returns>A <see cref="PreviewUpdateResult"/> with <c>IsPartialResult = true</c>.</returns>
    private static PreviewUpdateResult BuildPartialResult(
        bool isNewPackage,
        string reason)
    {
        return new PreviewUpdateResult(
            Added: [],
            Changed: [],
            Removed: [],
            ResolvedVersion: string.Empty,
            VersionNote: null,
            IsNewPackage: isNewPackage,
            IsPartialResult: true,
            PartialResultReason: reason);
    }

    #endregion
}
