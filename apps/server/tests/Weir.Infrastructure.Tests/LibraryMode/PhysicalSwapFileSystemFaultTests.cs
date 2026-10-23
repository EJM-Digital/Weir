using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>The operating system's own errors at each call the swap makes, so a held, busy or cross-volume file is named for what it is.</summary>
public sealed class PhysicalSwapFileSystemFaultTests : IDisposable
{
    private const int Ebusy = 16;
    private const int Exdev = 18;

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Write(string name, string content = "film")
    {
        var path = _folder.Join(name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The error the system gives for a file another program holds: a sharing violation on Windows, EBUSY elsewhere.</summary>
    private static Exception Held(string path) =>
        OperatingSystem.IsWindows() ? FaultingFileSystem.SharingViolation(path) : new IOException($"Device or resource busy: '{path}'", Ebusy);

    [Fact]
    public void A_delete_of_a_file_the_system_says_another_program_holds_is_named_as_in_use_and_the_file_stays()
    {
        var path = Write("Film.mkv");
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.DeleteFile, Held));

        var error = Assert.Throws<FileInUseException>(() => swap.Delete(path));

        Assert.IsAssignableFrom<IOException>(error.InnerException);
        Assert.Equal("film", File.ReadAllText(path));
    }

    [Fact]
    public void A_write_probe_that_cannot_be_cleaned_up_still_reports_the_folder_as_writable()
    {
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.DeleteFile, FaultingFileSystem.SharingViolation));

        swap.ProbeWrite(_folder.Path);

        Assert.Single(Directory.GetFiles(_folder.Path, ".weir-write-test-*"));
    }

    [Fact]
    public void A_write_probe_the_folder_refuses_is_the_error_the_system_gave_and_the_cleanup_is_still_tried()
    {
        var files = new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.Refused);
        var swap = new PhysicalSwapFileSystem(files);

        Assert.Throws<UnauthorizedAccessException>(() => swap.ProbeWrite(_folder.Path));

        Assert.Contains(files.Calls, call => call.Call == FileCall.DeleteFile);
        Assert.Empty(Directory.GetFiles(_folder.Path));
    }

    [Fact]
    public void A_reservation_that_cannot_be_made_because_the_name_is_taken_is_declined()
    {
        var taken = Write("Film.mkv");
        var swap = new PhysicalSwapFileSystem();

        Assert.False(swap.TryReserve(taken));
        Assert.Equal("film", File.ReadAllText(taken));
    }

    [Fact]
    public void A_reservation_the_folder_refuses_is_the_error_the_system_gave()
    {
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.OpenFile, FaultingFileSystem.Refused));

        Assert.Throws<UnauthorizedAccessException>(() => swap.TryReserve(_folder.Join("Film.mkv")));

        Assert.Empty(Directory.GetFiles(_folder.Path));
    }

    [Fact]
    public void A_copy_over_a_reservation_that_fails_is_the_error_the_system_gave_and_the_source_is_intact()
    {
        var source = Write("clean.mkv", "cleaned");
        var reservation = Write("Film.mkv", "");
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.CopyFile, FaultingFileSystem.SharingViolation));

        Assert.Throws<IOException>(() => swap.CopyOverReservation(source, reservation));

        Assert.Equal("cleaned", File.ReadAllText(source));
        Assert.Empty(File.ReadAllText(reservation));
    }

    [PosixFact("The Windows move goes straight to the operating system.")]
    public void A_move_the_system_says_is_busy_is_named_as_in_use_and_nothing_moves()
    {
        var source = Write("Film.mkv");
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.MoveFile, Held));

        Assert.Throws<FileInUseException>(() => swap.Move(source, _folder.Join("Moved.mkv")));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(_folder.Join("Moved.mkv")));
    }

    [PosixFact("The Windows move goes straight to the operating system.")]
    public void A_move_to_another_volume_is_named_as_cross_volume_and_nothing_moves()
    {
        var source = Write("Film.mkv");
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.MoveFile, path => new IOException($"Invalid cross-device link: '{path}'", Exdev)));

        Assert.Throws<CrossVolumeException>(() => swap.Move(source, _folder.Join("Moved.mkv")));

        Assert.True(File.Exists(source));
    }

    [PosixFact("The Windows replace goes straight to the operating system.")]
    public void A_replace_the_system_says_is_busy_is_named_as_in_use_and_the_reservation_stays()
    {
        var source = Write("clean.mkv", "cleaned");
        var reservation = Write("Film.mkv", "");
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.MoveFile, Held));

        Assert.Throws<FileInUseException>(() => swap.ReplaceReservation(source, reservation));

        Assert.Equal("cleaned", File.ReadAllText(source));
        Assert.Empty(File.ReadAllText(reservation));
    }

    [PosixFact("The Windows replace goes straight to the operating system.")]
    public void A_replace_onto_another_volume_is_named_as_cross_volume_and_the_reservation_stays()
    {
        var source = Write("clean.mkv", "cleaned");
        var reservation = Write("Film.mkv", "");
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.MoveFile, path => new IOException($"Invalid cross-device link: '{path}'", Exdev)));

        Assert.Throws<CrossVolumeException>(() => swap.ReplaceReservation(source, reservation));

        Assert.Empty(File.ReadAllText(reservation));
    }

    [PosixFact("The free-space reading on Windows is the operating system's own call.")]
    public void A_free_space_reading_the_system_refuses_is_no_reading()
    {
        var swap = new PhysicalSwapFileSystem(new FaultingFileSystem().Fail(FileCall.GetAvailableFreeSpace, FaultingFileSystem.Refused));

        Assert.Null(swap.AvailableFreeBytes(_folder.Path));
    }

    [Fact]
    public void Files_that_only_look_like_leftovers_are_not_listed_as_leftovers()
    {
        var swap = new PhysicalSwapFileSystem();
        Directory.CreateDirectory(_folder.Join("a"));
        File.WriteAllText(_folder.Join("a", "Film.mkv"), "film");
        File.WriteAllText(_folder.Join("a", "Film.weir-unrelated"), "x");

        Assert.Empty(swap.EnumerateLeftovers(_folder.Path));
    }
}
