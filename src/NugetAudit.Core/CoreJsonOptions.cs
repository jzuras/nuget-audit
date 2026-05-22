using System.Text.Json;

namespace NugetAudit.Core;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances used across Core for NuGet API response deserialization.
/// </summary>
internal static class CoreJsonOptions
{
    /// <summary>
    /// Gets JSON options with case-insensitive property matching.
    /// Used when deserializing NuGet API responses, which may vary between camelCase and PascalCase.
    /// </summary>
    internal static JsonSerializerOptions CaseInsensitive { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
