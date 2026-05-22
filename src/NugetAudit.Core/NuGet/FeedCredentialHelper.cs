using System.Net.Http.Headers;
using System.Text;
using NugetAudit.Core.Models;

namespace NugetAudit.Core.NuGet;

/// <summary>
/// Shared HTTP credential utilities for NuGet feed requests.
/// </summary>
internal static class FeedCredentialHelper
{
    /// <summary>
    /// Adds the appropriate Authorization header based on the credential's auth scheme.
    /// Basic auth: base64-encoded username:password. Bearer auth: token in the Password field.
    /// </summary>
    /// <param name="request">The HTTP request to modify.</param>
    /// <param name="credential">The feed credential; null is a no-op.</param>
    internal static void AddCredentialHeaders(HttpRequestMessage request, FeedCredential? credential)
    {
        if (credential is null)
        {
            return;
        }

        if (credential.AuthScheme == FeedAuthScheme.Bearer)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", credential.Password);
            return;
        }

        // Basic auth.
        string encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}"));

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }
}
