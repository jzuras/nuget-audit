using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NugetAudit.Core.Audit;
using NugetAudit.Core.Configuration;
using NugetAudit.Core.DependencyGraph;
using NugetAudit.Core.NuGet;
using NugetAudit.Core.Security;
using NugetAudit.Core.Services;
using NugetAudit.Core.Trust;

namespace NugetAudit.Core;

/// <summary>
/// Extension methods for registering NugetAudit.Core services with an <see cref="IServiceCollection"/>.
/// Shared by both CLI and Blazor GUI modes.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all NugetAudit.Core services including NuGet API clients, trust evaluation,
    /// trust config I/O, security advisory checks, and the audit runner.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddNugetAuditCoreServices(this IServiceCollection services)
    {
        // NuGet Registration API client — requires GZip decompression for registration5-gz-semver2.
        services
            .AddHttpClient<INuGetRegistrationClient, NuGetRegistrationClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        // NuGet Search API client — public nuget.org search endpoint, no special encoding.
        services.AddHttpClient<INuGetSearchClient, NuGetSearchClient>();

        // Trust evaluation and configuration.
        services.AddSingleton<ITrustEvaluator, TrustEvaluator>();
        services.AddSingleton<ITrustConfigLoader, TrustConfigLoader>();
        services.AddSingleton<ITrustConfigSaver, TrustConfigSaver>();

        // Security advisory checks.
        services.AddSingleton<ISecurityAdvisoryService, SecurityAdvisoryService>();

        // Audit orchestration.
        services.AddSingleton<IAuditRunner, AuditRunner>();

        // Preview flows (Phase 3).
        services.AddSingleton<IVersionRangeResolver, VersionRangeResolver>();
        services.AddSingleton<IProjectFileParser, ProjectFileParser>();
        services.AddHttpClient<IPrivateFeedResolver, PrivateFeedResolver>();
        services.AddSingleton<IPreviewService, PreviewService>();

        return services;
    }
}
