using Weir.Infrastructure.Browse;

namespace Weir.Infrastructure.Tests.Browse;

/// <summary>
/// #723: <see cref="DirectoryBrowser"/> rejects UNC paths, device paths and alternate data streams before any
/// filesystem call, so a path like <c>\\host\share</c> can never reach <c>CreateFileW</c> and make the server
/// connect to a remote host. <see cref="DirectoryBrowser.Browse(string?)"/> and <see cref="DirectoryBrowser.ValidateFilePath"/>
/// share one validator (<see cref="DirectoryBrowser.RejectUnsafePathForm"/>), proved directly here and through
/// both public entry points.
/// </summary>
public sealed class DirectoryBrowserTests
{
    [Theory]
    [InlineData(@"\\host\share")]
    [InlineData("//host/share")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\host@80\share")]
    public void A_unc_or_device_path_is_rejected_before_any_filesystem_call(string path)
    {
        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.RejectUnsafePathForm(path));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("UNC and device paths are not allowed.", exception.Message);
    }

    [Fact]
    public void An_ordinary_rooted_path_passes_the_unsafe_form_check()
    {
        DirectoryBrowser.RejectUnsafePathForm(OperatingSystem.IsWindows() ? @"C:\Media\Movies" : "/media/movies");
    }

    [Fact]
    public void Browse_rejects_a_unc_path_without_touching_the_filesystem()
    {
        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.Browse(@"\\attacker.example\share"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("UNC and device paths are not allowed.", exception.Message);
    }

    [Fact]
    public void ValidateFilePath_rejects_a_unc_path_without_touching_the_filesystem()
    {
        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.ValidateFilePath(@"\\attacker.example\share\file.mkv"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("UNC and device paths are not allowed.", exception.Message);
    }

    [Theory]
    [InlineData(@"C:\Media\Movies:hidden-stream")]
    [InlineData(@"Movies:stream")]
    public void An_alternate_data_stream_is_rejected_on_windows(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Assert.Throws<DirectoryBrowseException>(() => DirectoryBrowser.RejectUnsafePathForm(path));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("Alternate data streams are not allowed.", exception.Message);
    }

    [Fact]
    public void A_drive_letter_colon_is_not_mistaken_for_an_alternate_data_stream()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DirectoryBrowser.RejectUnsafePathForm(@"C:\Media\Movies");
    }
}
