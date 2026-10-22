using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>The real filesystem operations the swap uses, one at a time, on real files in a temporary folder.</summary>
public sealed class PhysicalSwapFileSystemTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    private static PhysicalSwapFileSystem Files => PhysicalSwapFileSystem.Instance;

    public void Dispose() => _folder.Dispose();

    private string Write(string name, string content)
    {
        var path = _folder.Join(name);
        File.WriteAllText(path, content);
        return path;
    }

    public static TheoryData<Func<Exception>, Func<Exception, Exception>, Func<string, Exception>> ExceptionShapes() => new()
    {
        { () => new FileInUseException(), inner => new FileInUseException("held", inner), message => new FileInUseException(message) },
        { () => new CrossVolumeException(), inner => new CrossVolumeException("apart", inner), message => new CrossVolumeException(message) },
    };

    [Theory]
    [MemberData(nameof(ExceptionShapes))]
    public void The_swap_exceptions_carry_a_message_and_the_error_that_caused_them(
        Func<Exception> plain,
        Func<Exception, Exception> wrapping,
        Func<string, Exception> withMessage)
    {
        var cause = new IOException("the cause");

        Assert.IsAssignableFrom<IOException>(plain());
        Assert.Equal("a message", withMessage("a message").Message);
        Assert.Same(cause, wrapping(cause).InnerException);
    }

    [Fact]
    public void The_content_hash_is_the_SHA256_of_the_whole_file()
    {
        var path = Write("abc.bin", "abc");

        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", Files.ContentHash(path));
        Assert.NotEqual(Files.ContentHash(path), Files.ContentHash(Write("abd.bin", "abd")));
    }

    [Fact]
    public void A_reservation_creates_an_empty_file_once_and_is_refused_while_it_exists()
    {
        var path = _folder.Join("reserved.mkv");

        Assert.True(Files.TryReserve(path));
        Assert.Equal(0, new FileInfo(path).Length);
        File.WriteAllText(path, "claimed");

        Assert.False(Files.TryReserve(path));
        Assert.Equal("claimed", File.ReadAllText(path));
    }

    [Fact]
    public void A_reservation_is_replaced_by_the_cleaned_file()
    {
        var reservation = _folder.Join("kept.mkv");
        Assert.True(Files.TryReserve(reservation));
        var source = Write("source.mkv", "the original");

        Files.ReplaceReservation(source, reservation);

        Assert.Equal("the original", File.ReadAllText(reservation));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void A_reservation_is_not_replaced_by_a_file_that_is_not_there()
    {
        var reservation = _folder.Join("kept.mkv");
        Assert.True(Files.TryReserve(reservation));

        Assert.ThrowsAny<IOException>(() => Files.ReplaceReservation(_folder.Join("missing.mkv"), reservation));

        Assert.True(File.Exists(reservation));
    }

    [Fact]
    public void A_copy_over_a_reservation_leaves_the_source_in_place()
    {
        var reservation = _folder.Join("kept.mkv");
        Assert.True(Files.TryReserve(reservation));
        var source = Write("source.mkv", "the original");

        Files.CopyOverReservation(source, reservation);

        Assert.Equal("the original", File.ReadAllText(reservation));
        Assert.Equal("the original", File.ReadAllText(source));
    }

    [Fact]
    public void Deleting_a_file_that_is_not_there_is_not_an_error()
    {
        Files.Delete(_folder.Join("never-existed.mkv"));
    }

    [Fact]
    public void A_delete_that_fails_for_another_reason_is_not_called_a_file_in_use()
    {
        var exception = Assert.ThrowsAny<IOException>(() => Files.Delete(_folder.Join("missing-folder", "file.mkv")));

        Assert.IsNotType<FileInUseException>(exception);
    }

    [Fact]
    public void A_missing_folder_has_no_free_space_reading()
    {
        Assert.Null(Files.AvailableFreeBytes(_folder.Join("missing-folder")));
    }

    [Fact]
    public void A_file_that_is_there_is_found_and_fingerprinted()
    {
        var path = Write("movie.mkv", "content");

        Assert.True(Files.FileExists(path));
        Assert.False(Files.FileExists(_folder.Join("other.mkv")));
        Assert.Equal(7, Files.Fingerprint(path).SizeBytes);
    }

    [WindowsFact("A held file is refused by name only where Windows enforces share modes")]
    public void A_delete_of_a_file_another_program_holds_says_it_is_in_use()
    {
        var path = Write("held.mkv", "content");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<FileInUseException>(() => Files.Delete(path));
        Assert.True(Files.IsInUse(path));
        Assert.True(File.Exists(path));
    }

    [WindowsFact("A held file is refused by name only where Windows enforces share modes")]
    public void A_rename_onto_a_reservation_of_a_file_another_program_holds_says_it_is_in_use()
    {
        var reservation = _folder.Join("kept.mkv");
        Assert.True(Files.TryReserve(reservation));
        var source = Write("source.mkv", "the original");
        using var holder = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<FileInUseException>(() => Files.ReplaceReservation(source, reservation));
        Assert.Throws<FileInUseException>(() => Files.Move(source, _folder.Join("moved.mkv")));
    }

    [WindowsFact("Asking Windows whether a file is held fails by name only on Windows")]
    public void Asking_whether_a_missing_file_is_in_use_says_the_file_is_not_there()
    {
        Assert.Throws<FileNotFoundException>(() => Files.IsInUse(_folder.Join("missing.mkv")));
        Assert.Throws<FileNotFoundException>(() => Files.LinkCount(_folder.Join("missing.mkv")));
    }

    [WindowsFact("Only Windows has a second volume the rename can be refused across")]
    public void A_rename_to_another_volume_is_refused_as_a_cross_volume_move()
    {
        var sibling = OtherWritableDrive();
        if (sibling is null)
        {
            return;
        }

        var target = Path.Join(sibling, $"weir-cross-volume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        try
        {
            var source = Write("source.mkv", "content");
            var reservation = Path.Join(target, "kept.mkv");
            Assert.True(Files.TryReserve(reservation));

            Assert.Throws<CrossVolumeException>(() => Files.Move(source, Path.Join(target, "moved.mkv")));
            Assert.Throws<CrossVolumeException>(() => Files.ReplaceReservation(source, reservation));
            Assert.Equal("content", File.ReadAllText(source));
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    private string? OtherWritableDrive()
    {
        var here = Path.GetPathRoot(_folder.Path);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady || string.Equals(drive.Name, here, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Files.ProbeWrite(drive.Name);
                return drive.Name;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
