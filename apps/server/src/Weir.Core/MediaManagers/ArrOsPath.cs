namespace Weir.Core.MediaManagers;

/// <summary>
/// Sonarr/Radarr's own path comparison (<c>NzbDrone.Common/Disk/OsPath.cs</c>, identical on Sonarr develop, Sonarr
/// v5-develop and Radarr develop), mirrored so Weir judges a remote path mapping exactly the way the manager will apply it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Kind (<c>DetectPathKind</c>, L43-61): a path starting with <c>/</c> is Unix; otherwise a drive letter or any
/// <c>\</c> makes it Windows; otherwise any <c>/</c> makes it Unix; otherwise it is unknown.</item>
/// <item>Rooted (<c>IsRooted</c>, L133-149): Windows needs <c>\\</c> or a drive letter, Unix a leading <c>/</c>, unknown
/// never is.</item>
/// <item>Contains (<c>Contains</c>, L344-370): both rooted; split on both separators with empty segments dropped
/// (<c>GetFragments</c>, L294-297), so trailing and doubled slashes never matter; the other path has at least as many
/// segments and starts with every one of these; segments compare ignoring case only when either path is a Windows path
/// (L359). So on Linux <c>/Media</c> and <c>/media</c> differ, and <c>/downloads/complete2</c> is not inside
/// <c>/downloads/complete</c>.</item>
/// </list>
/// </remarks>
public readonly record struct ArrOsPath(string Text)
{
    private static readonly char[] Separators = ['\\', '/'];

    public bool IsWindows => Kind == PathKind.Windows;

    public PathKind Kind
    {
        get
        {
            var path = Text ?? string.Empty;
            if (path.StartsWith('/'))
            {
                return PathKind.Unix;
            }

            if (HasDriveLetter(path) || path.Contains('\\', StringComparison.Ordinal))
            {
                return PathKind.Windows;
            }

            return path.Contains('/', StringComparison.Ordinal) ? PathKind.Unix : PathKind.Unknown;
        }
    }

    public bool IsRooted => Kind switch
    {
        PathKind.Windows => Text.StartsWith(@"\\", StringComparison.Ordinal) || HasDriveLetter(Text),
        PathKind.Unix => Text.StartsWith('/'),
        _ => false,
    };

    public string[] Segments => (Text ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary><c>OsPath.Contains</c>: this path is <paramref name="other"/> or one of its parent folders.</summary>
    public bool Contains(ArrOsPath other)
    {
        if (!IsRooted || !other.IsRooted)
        {
            return false;
        }

        var left = Segments;
        var right = other.Segments;
        if (right.Length < left.Length)
        {
            return false;
        }

        var comparison = Comparison(this, other);
        for (var i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i], right[i], comparison))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The same folder by <c>Contains</c> both ways: Sonarr never compares two whole paths in this flow.</summary>
    public bool SameFolder(ArrOsPath other) => Contains(other) && other.Contains(this);

    /// <summary>
    /// <c>LocalPath + (path - RemotePath)</c> (<c>RemotePathMappingService.RemapRemoteToLocal</c> L144): where a path the
    /// download client reports ends up once the mapping is applied. Only meaningful when <paramref name="remote"/> contains
    /// this path.
    /// </summary>
    public ArrOsPath Remap(ArrOsPath remote, ArrOsPath local)
    {
        var rest = Segments.Skip(remote.Segments.Length).ToArray();
        var root = local.Text.TrimEnd('\\', '/');
        if (rest.Length == 0)
        {
            return root.Length == 0 ? local : new ArrOsPath(root);
        }

        // A mapping onto the filesystem root has no folder name left once its separators are trimmed; what is below it still follows.
        var separator = local.IsWindows ? "\\" : "/";
        return new ArrOsPath(root + separator + string.Join(separator, rest));
    }

    public override string ToString() => Text ?? string.Empty;

    // Sonarr compares with the invariant culture (OsPath.cs L359), not ordinally; kept identical on purpose.
#pragma warning disable CA1309
    private static StringComparison Comparison(ArrOsPath left, ArrOsPath right) =>
        left.IsWindows || right.IsWindows ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture;
#pragma warning restore CA1309

    private static bool HasDriveLetter(string path) =>
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] is '\\' or '/');

    public enum PathKind
    {
        Unknown,
        Windows,
        Unix,
    }
}
