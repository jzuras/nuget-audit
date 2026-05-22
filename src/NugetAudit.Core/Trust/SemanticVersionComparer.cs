namespace NugetAudit.Core.Trust;

/// <summary>
/// Compares two NuGet version strings semantically using numeric component comparison.
/// </summary>
/// <remarks>
/// Needed because lexicographic comparison gives wrong results for multi-digit version
/// components (e.g. "9.0.3" &lt; "9.0.10" numerically but NOT lexicographically).
/// </remarks>
public static class SemanticVersionComparer
{
    /// <summary>
    /// Compares two NuGet version strings semantically.
    /// Returns a negative number if <paramref name="a"/> is less than <paramref name="b"/>,
    /// zero if equal, or a positive number if greater.
    /// Strips pre-release suffixes before comparing numeric components.
    /// Falls back to ordinal string comparison if parsing fails.
    /// </summary>
    /// <param name="a">The first version string.</param>
    /// <param name="b">The second version string.</param>
    /// <returns>
    /// Negative if a &lt; b, 0 if equal, positive if a &gt; b.
    /// </returns>
    public static int Compare(string a, string b)
    {
        try
        {
            string numericA = StripPreReleaseSuffix(a);
            string numericB = StripPreReleaseSuffix(b);

            var va = System.Version.Parse(numericA);
            var vb = System.Version.Parse(numericB);

            return va.CompareTo(vb);
        }
        catch
        {
            return string.CompareOrdinal(a, b);
        }
    }

    /// <summary>
    /// Strips any pre-release suffix (starting at the first '+' or '-') from a version string.
    /// </summary>
    /// <param name="version">The version string to strip.</param>
    /// <returns>The numeric-only portion of the version string.</returns>
    private static string StripPreReleaseSuffix(string version)
    {
        int idx = version.IndexOfAny(['+', '-']);

        return idx >= 0 ? version[..idx] : version;
    }
}
