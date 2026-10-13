using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Platform;

public sealed class LanAccessFileTests : IDisposable
{
    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    private LanAccessFile Saved() => new(_home.Path);

    private string SavedPath => _home.Join(LanAccessFile.FileName);

    [Fact]
    public void With_nothing_saved_there_is_no_choice()
    {
        Assert.Null(Saved().Read());
    }

    [Fact]
    public void With_nothing_saved_nothing_waits_to_find_that_out()
    {
        Assert.Null(new LanAccessFile(_home.Path, _ => throw new InvalidOperationException("Nothing waits for a file that is not there.")).Read());
    }

    [Theory]
    [InlineData(NetworkScope.Network, "on")]
    [InlineData(NetworkScope.ThisPcOnly, "off")]
    public void A_choice_is_saved_in_the_words_the_tray_reads(NetworkScope scope, string text)
    {
        Saved().Write(scope);

        Assert.Equal(text, File.ReadAllText(SavedPath));
        Assert.Equal(scope, Saved().Read());
    }

    [Fact]
    public void Text_the_tray_would_not_understand_reads_as_this_pc_only()
    {
        File.WriteAllText(SavedPath, "maybe");

        Assert.Equal(NetworkScope.ThisPcOnly, Saved().Read());
    }

    [Fact]
    public void Surrounding_whitespace_in_the_saved_text_is_ignored()
    {
        File.WriteAllText(SavedPath, " on\r\n");

        Assert.Equal(NetworkScope.Network, Saved().Read());
    }

    [Fact]
    public void Saving_the_same_choice_again_replaces_the_file_so_the_tray_sees_a_new_request()
    {
        Saved().Write(NetworkScope.Network);
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(SavedPath, longAgo);

        Saved().Write(NetworkScope.Network);

        Assert.True(File.GetLastWriteTimeUtc(SavedPath) > longAgo);
    }

    [Fact]
    public void A_file_that_is_refused_for_a_moment_is_the_last_good_choice_and_nothing_waits()
    {
        var file = new LanAccessFile(_home.Path, _ => throw new InvalidOperationException("A read does not wait."));
        file.Write(NetworkScope.Network);
        Assert.Equal(NetworkScope.Network, file.Read());

        using (new FileStream(SavedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(NetworkScope.Network, file.Read());
        }

        file.Write(NetworkScope.ThisPcOnly);
        Assert.Equal(NetworkScope.ThisPcOnly, file.Read());
    }

    [Fact]
    public void A_file_that_is_refused_before_any_choice_was_read_is_this_pc_only_and_nothing_waits()
    {
        Saved().Write(NetworkScope.Network);

        using (new FileStream(SavedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(NetworkScope.ThisPcOnly, new LanAccessFile(_home.Path, _ => throw new InvalidOperationException("A read does not wait.")).Read());
        }

        Assert.Equal(NetworkScope.Network, Saved().Read());
    }

    [Fact]
    public void A_choice_that_was_removed_after_it_was_read_is_none_again()
    {
        var file = new LanAccessFile(_home.Path, _ => throw new InvalidOperationException("A read does not wait."));
        file.Write(NetworkScope.Network);
        Assert.Equal(NetworkScope.Network, file.Read());

        File.Delete(SavedPath);

        Assert.Null(file.Read());
    }

    [Fact]
    public void Saving_leaves_no_scratch_file_behind()
    {
        Saved().Write(NetworkScope.Network);

        var names = Directory.GetFiles(_home.Path).Select(file => new FileInfo(file).Name).ToArray();
        Assert.Equal([LanAccessFile.FileName], names);
    }
}
