namespace Weir.Infrastructure.IO;

/// <summary>How paths are compared: exactly on a system that tells <c>A</c> from <c>a</c>, ignoring case on one that does not.</summary>
internal static class PathCase
{
    public static StringComparison PathComparison(bool caseSensitive) =>
        caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer PathComparer(bool caseSensitive) =>
        caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
