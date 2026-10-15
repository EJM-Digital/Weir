using System.Text;
using FsCheck;
using FsCheck.Fluent;

namespace Weir.TestSupport;

/// <summary>
/// Generators for the paths a media manager sends and Weir stores: names, separators of either kind, doubled and trailing
/// separators, drive letters, shares, and a name written in a different case.
/// </summary>
internal static class PathGenerators
{
    /// <summary>How a name is written: as it is, in capitals or in lower case.</summary>
    public enum CaseStyle
    {
        Keep,
        Upper,
        Lower,
    }

    /// <summary>The starts a path can have: none, either separator, a drive, a share.</summary>
    public static readonly string[] Roots = ["/", "\\", "C:\\", "d:/", "//nas/media/", "\\\\nas\\media\\", string.Empty];

    private static readonly char[] AlphaNumeric = [.. "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789éüñ"];

    private static readonly char[] InsideName = [.. "abcdefghijklmnopqrstuvwxyz0123456789 ._-()[]"];

    /// <summary>One file or folder name: never empty, never <c>.</c> or <c>..</c>, never starting or ending with a space.</summary>
    public static readonly Gen<string> Name =
        from first in Gen.Elements(AlphaNumeric)
        from length in Gen.Choose(0, 7)
        from middle in Gen.ArrayOf(Gen.Elements(InsideName), length)
        from last in Gen.Elements(AlphaNumeric)
        select length == 0 ? first.ToString() : first + new string(middle) + last;

    /// <summary>A separator as a manager's host might write it: either kind, sometimes doubled or mixed.</summary>
    public static readonly Gen<string> Separator = Gen.Elements("/", "\\", "/", "\\", "//", "\\\\", "/\\");

    /// <summary>A part of a path that may be a name, <c>..</c>, <c>.</c> or empty, so a generated path can wander.</summary>
    public static readonly Gen<string> WanderingPart =
        Gen.Frequency((6, Name), (2, Gen.Constant("..")), (2, Gen.Constant(".")), (1, Gen.Constant(string.Empty)));

    public static Gen<IReadOnlyList<string>> Names(int minimum, int maximum) =>
        from count in Gen.Choose(minimum, maximum)
        from names in Gen.ArrayOf(Name, count)
        select (IReadOnlyList<string>)names;

    /// <summary>The same start written another way: another case, or the other kind of separator.</summary>
    public static Gen<string> Variant(string root) =>
        Gen.Elements(root, root.ToUpperInvariant(), root.ToLowerInvariant(), root.Replace('\\', '/'), root.Replace('/', '\\'));

    /// <summary>
    /// <paramref name="parts"/> joined with generated separators after <paramref name="root"/>, optionally each in a generated case and
    /// optionally followed by a separator.
    /// </summary>
    public static Gen<string> Render(string root, IReadOnlyList<string> parts, bool varyCase, bool trailingSeparator = false) =>
        from separators in Gen.ArrayOf(Separator, parts.Count + 1)
        from styles in Gen.ArrayOf(varyCase ? Gen.Elements(CaseStyle.Keep, CaseStyle.Upper, CaseStyle.Lower) : Gen.Constant(CaseStyle.Keep), parts.Count)
        from trailing in Gen.Elements(trailingSeparator, false)
        select Join(root, parts, separators, styles, trailing);

    /// <summary>The text with a generated run of white space on each side.</summary>
    public static Gen<string> Padded(string text) =>
        from before in Gen.Elements(string.Empty, string.Empty, " ", "\t", "\u00a0")
        from after in Gen.Elements(string.Empty, string.Empty, " ", "\r\n", "\u3000")
        select before + text + after;

    public static string Written(CaseStyle style, string name) => style switch
    {
        CaseStyle.Upper => name.ToUpperInvariant(),
        CaseStyle.Lower => name.ToLowerInvariant(),
        _ => name,
    };

    private static string Join(string root, IReadOnlyList<string> parts, string[] separators, CaseStyle[] styles, bool trailing)
    {
        var builder = new StringBuilder(root);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(separators[i]);
            }

            builder.Append(Written(styles[i], parts[i]));
        }

        if (trailing && parts.Count > 0)
        {
            builder.Append(separators[parts.Count]);
        }

        return builder.ToString();
    }
}
