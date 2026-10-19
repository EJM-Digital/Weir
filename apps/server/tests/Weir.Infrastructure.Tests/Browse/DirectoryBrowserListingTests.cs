using Weir.Core.Json;
using Weir.Infrastructure.Browse;

namespace Weir.Infrastructure.Tests.Browse;

/// <summary>
/// Real folders in a temp directory: what the folder picker lists, how it answers for a path that is missing,
/// relative or off every drive, and how a link is resolved.
/// </summary>
public sealed class DirectoryBrowserListingTests
{
    private static string Text(WireObject o, string key) => ((WireString)o.Get(key)!).Value;

    private static List<WireObject> Entries(WireObject listing) => ((WireArray)listing.Get("entries")!).Items.Cast<WireObject>().ToList();

    /// <summary>A folder's path as the picker reports it: resolved, with a trailing backslash on Windows.</summary>
    private static string Reported(string folder)
    {
        var real = DirectoryBrowser.RealPath(folder);
        return OperatingSystem.IsWindows() ? real.TrimEnd('\\', '/') + "\\" : real;
    }

    private static string AnUnusedDriveRoot() =>
        Enumerable.Range('A', 26).Select(c => $"{(char)c}:\\").First(root => !Directory.Exists(root));

    [Fact]
    public void Every_way_to_build_the_exception_keeps_its_message_and_status()
    {
        var inner = new IOException("disk");

        Assert.Equal(0, new DirectoryBrowseException().StatusCode);
        Assert.Equal("m", new DirectoryBrowseException("m").Message);
        Assert.Same(inner, new DirectoryBrowseException("m", inner).InnerException);
        var withStatus = new DirectoryBrowseException(404, "gone");
        Assert.Equal((404, "gone"), (withStatus.StatusCode, withStatus.Message));
        var withInner = new DirectoryBrowseException(403, "no", inner);
        Assert.Equal((403, "no"), (withInner.StatusCode, withInner.Message));
        Assert.Same(inner, withInner.InnerException);
    }

    [Fact]
    public void A_folder_lists_only_its_subfolders_in_name_order_ignoring_case()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Join("beta"));
        Directory.CreateDirectory(dir.Join("Alpha"));
        Directory.CreateDirectory(dir.Join("gamma"));
        File.WriteAllBytes(dir.Join("a-file.mkv"), [1]);

        var listing = DirectoryBrowser.Browse(dir.Path);

        Assert.Equal(["Alpha", "beta", "gamma"], Entries(listing).Select(e => Text(e, "name")));
        Assert.All(Entries(listing), e => Assert.Equal("directory", Text(e, "kind")));
        Assert.Equal(Reported(dir.Join("Alpha")), Text(Entries(listing)[0], "path"));
        Assert.Equal(Reported(dir.Path), Text(listing, "current_path"));
        Assert.Equal(Reported(Path.GetDirectoryName(dir.Path)!), Text(listing, "parent_path"));
        Assert.IsType<WireNull>(Entries(listing)[0].Get("description"));
    }

    [Fact]
    public void A_nul_character_in_the_path_is_dropped_before_it_is_used()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Join("films"));

        var listing = DirectoryBrowser.Browse(dir.Path + "\0");

        Assert.Equal(["films"], Entries(listing).Select(e => Text(e, "name")));
    }

    [Fact]
    public void A_relative_path_is_refused_as_not_absolute()
    {
        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse("movies/new"));

        Assert.Equal((400, "Path must be absolute."), (exception.StatusCode, exception.Message));
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_a_404()
    {
        using var dir = new TempDirectory();

        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse(dir.Join("missing")));

        Assert.Equal((404, "The requested directory does not exist."), (exception.StatusCode, exception.Message));
    }

    [Fact]
    public void A_file_is_not_a_folder()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.Join("film.mkv"), [1]);

        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse(dir.Join("film.mkv")));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public void A_folder_the_server_may_not_read_is_a_403_that_keeps_the_system_error()
    {
        using var dir = new TempDirectory();
        var denied = new UnauthorizedAccessException("denied");

        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse(dir.Path, _ => throw denied));

        Assert.Equal((403, "Weir cannot access this folder."), (exception.StatusCode, exception.Message));
        Assert.Same(denied, exception.InnerException);
    }

    [Fact]
    public void A_folder_that_fails_while_it_is_read_is_a_400_that_keeps_the_system_error()
    {
        using var dir = new TempDirectory();
        var failure = new IOException("device gone");

        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse(dir.Path, _ => throw failure));

        Assert.Equal((400, "The requested directory cannot be accessed."), (exception.StatusCode, exception.Message));
        Assert.Same(failure, exception.InnerException);
    }

    [Fact]
    public void An_entry_that_vanishes_before_it_is_looked_at_is_left_out()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Join("kept"));

        var listing = DirectoryBrowser.Browse(dir.Path, folder => [dir.Join("kept"), dir.Join("gone")]);

        Assert.Equal(["kept"], Entries(listing).Select(e => Text(e, "name")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_path_lists_the_places_to_start_from(string? path)
    {
        var entries = Entries(DirectoryBrowser.Browse(path));

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal("root", Text(e, "kind")));
        if (OperatingSystem.IsWindows())
        {
            var systemDrive = Path.GetPathRoot(Path.GetTempPath())!;
            var drive = Assert.Single(entries, e => string.Equals(Text(e, "path"), systemDrive, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(systemDrive.TrimEnd('\\'), Text(drive, "name"), ignoreCase: true);
            Assert.Equal("Local drive", Text(drive, "description"));
        }
        else
        {
            var root = Assert.Single(entries);
            Assert.Equal(("/", "/", "Filesystem root"), (Text(root, "name"), Text(root, "path"), Text(root, "description")));
        }
    }

    [Fact]
    public void The_top_of_the_filesystem_has_no_parent()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var listing = DirectoryBrowser.Browse(root);

        Assert.IsType<WireNull>(listing.Get("parent_path"));
        Assert.Equal(Reported(root), Text(listing, "current_path"));
    }

    [Theory]
    [InlineData(DriveType.Removable, "External drive")]
    [InlineData(DriveType.Fixed, "Local drive")]
    [InlineData(DriveType.Network, "Network drive")]
    [InlineData(DriveType.CDRom, "Optical drive")]
    [InlineData(DriveType.Ram, "RAM disk")]
    [InlineData(DriveType.NoRootDirectory, "Drive")]
    [InlineData(DriveType.Unknown, "Drive")]
    public void Each_kind_of_drive_is_described_in_plain_words(DriveType type, string expected)
    {
        Assert.Equal(expected, DirectoryBrowser.DescribeDrive(type));
    }

    [Theory]
    [InlineData(@"\\?\UNC\nas\media\films", @"\\nas\media\films")]
    [InlineData(@"\\?\C:\Media\Films", @"C:\Media\Films")]
    [InlineData(@"C:\Media\Films", @"C:\Media\Films")]
    public void A_final_path_loses_its_device_prefix_and_keeps_a_network_share_as_a_share(string finalPath, string expected)
    {
        Assert.Equal(expected, DirectoryBrowser.WithoutDevicePrefix(finalPath));
    }

    [Fact]
    public void A_file_path_is_returned_resolved()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.Join("film.mkv"), [1]);

        var resolved = DirectoryBrowser.ValidateFilePath(dir.Join("film.mkv"));

        Assert.True(File.Exists(resolved));
        Assert.Equal("film.mkv", Path.GetFileName(resolved));
        Assert.Equal(DirectoryBrowser.RealPath(dir.Path), Path.GetDirectoryName(resolved));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_file_path_is_refused(string? path)
    {
        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.ValidateFilePath(path));

        Assert.Equal((400, "A file path is required."), (exception.StatusCode, exception.Message));
    }

    [Fact]
    public void A_relative_file_path_is_refused_as_not_absolute()
    {
        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.ValidateFilePath("film.mkv"));

        Assert.Equal((400, "Path must be absolute."), (exception.StatusCode, exception.Message));
    }

    [Fact]
    public void A_file_that_is_not_there_and_a_folder_are_both_a_404()
    {
        using var dir = new TempDirectory();

        var missing = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.ValidateFilePath(dir.Join("missing.mkv")));
        var folder = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.ValidateFilePath(dir.Path));

        Assert.Equal((404, "The requested file does not exist."), (missing.StatusCode, missing.Message));
        Assert.Equal(404, folder.StatusCode);
    }

    [WindowsFact("Drive letters, the current drive and drive roots are Windows concepts.")]
    public void A_path_that_starts_at_the_current_drive_root_is_taken_as_absolute()
    {
        var listing = DirectoryBrowser.Browse(@"\");

        Assert.Equal(Reported(Path.GetPathRoot(Environment.CurrentDirectory)!), Text(listing, "current_path"));
    }

    [WindowsFact("Only Windows has a device that opens but has no file path.")]
    public void A_device_that_has_no_file_path_keeps_the_path_it_was_given()
    {
        Assert.Equal(@"\\.\NUL", DirectoryBrowser.RealPath(@"\\.\NUL"));
    }

    [WindowsFact("Drive letters are a Windows concept.")]
    public void A_drive_letter_alone_is_not_an_absolute_path()
    {
        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse("C:"));

        Assert.Equal((400, "Path must be absolute."), (exception.StatusCode, exception.Message));
    }

    [WindowsFact("Drive letters are a Windows concept.")]
    public void A_path_on_a_drive_that_is_not_there_is_refused_for_folders_and_files()
    {
        var offDrive = AnUnusedDriveRoot() + "films";

        var folder = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse(offDrive));
        var file = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.ValidateFilePath(offDrive + "\\film.mkv"));

        Assert.Equal((400, "Path is not within a valid drive root."), (folder.StatusCode, folder.Message));
        Assert.Equal(400, file.StatusCode);
    }

    [WindowsFact("Drive letters are a Windows concept.")]
    public void A_drive_letter_in_lower_case_is_the_same_drive()
    {
        using var dir = new TempDirectory();
        var lower = char.ToLowerInvariant(dir.Path[0]) + dir.Path[1..] + "\\missing";

        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse(lower));

        Assert.Equal(404, exception.StatusCode);
    }

    [PosixFact("The filesystem root is a POSIX concept.")]
    public void The_filesystem_root_is_its_own_top()
    {
        var listing = DirectoryBrowser.Browse("/");

        Assert.Equal("/", Text(listing, "current_path"));
        Assert.IsType<WireNull>(listing.Get("parent_path"));
    }

    [PosixFact("Symbolic links are created without special rights off Windows.")]
    public void A_link_to_a_folder_is_listed_and_browsed_by_where_it_leads()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Join("real", "inner"));
        Directory.CreateSymbolicLink(dir.Join("link"), dir.Join("real"));
        var real = DirectoryBrowser.RealPath(dir.Join("real"));

        var viaLink = DirectoryBrowser.Browse(dir.Join("link"));
        var parent = DirectoryBrowser.Browse(dir.Path);

        Assert.Equal(real, Text(viaLink, "current_path"));
        Assert.Equal(["inner"], Entries(viaLink).Select(e => Text(e, "name")));
        Assert.Equal(["link", "real"], Entries(parent).Select(e => Text(e, "name")));
        Assert.Equal([real, real], Entries(parent).Select(e => Text(e, "path")));
    }

    [PosixFact("Symbolic links are created without special rights off Windows.")]
    public void Links_that_lead_round_in_a_circle_are_left_as_written()
    {
        using var dir = new TempDirectory();
        Directory.CreateSymbolicLink(dir.Join("a"), dir.Join("b"));
        Directory.CreateSymbolicLink(dir.Join("b"), dir.Join("a"));

        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse(dir.Join("a")));

        Assert.Equal(404, exception.StatusCode);
    }

    [PosixFact("Permission bits are a POSIX concept.")]
    public void A_folder_that_cannot_be_searched_does_not_fail_the_path_inside_it()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        using var dir = new TempDirectory();
        var locked = dir.Join("locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.Equal(Path.Join(DirectoryBrowser.RealPath(dir.Path), "locked", "inside"), DirectoryBrowser.RealPath(dir.Join("locked", "inside")));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
