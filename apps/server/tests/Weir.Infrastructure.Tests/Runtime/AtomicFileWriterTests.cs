using System.Text;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// Windows refuses to rename a file over one that another process has open, so the writer looks again before it gives up. Time is not
/// involved: the pause between tries is where a test lets go of the file.
/// </summary>
public sealed class AtomicFileWriterTests : IDisposable
{
    private const string FileName = "saved.json";

    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    private string FilePath => _home.Join(FileName);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void A_file_is_written_and_replaced()
    {
        AtomicFileWriter.Replace(_home.Path, FileName, Bytes("first"));
        AtomicFileWriter.Replace(_home.Path, FileName, Bytes("second"));

        Assert.Equal("second", File.ReadAllText(FilePath));
        Assert.Equal([FilePath], Directory.GetFileSystemEntries(_home.Path));
    }

    [Fact]
    public void A_file_held_open_when_the_write_starts_is_replaced_once_it_is_let_go()
    {
        File.WriteAllText(FilePath, "old");
        var holder = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var pauses = 0;

        AtomicFileWriter.Replace(_home.Path, FileName, Bytes("new"), _ =>
        {
            pauses++;
            holder.Dispose();
        });

        Assert.Equal(1, pauses);
        Assert.Equal("new", File.ReadAllText(FilePath));
        Assert.Equal([FilePath], Directory.GetFileSystemEntries(_home.Path));
    }

    [Fact]
    public void A_file_that_stays_held_open_is_given_up_on_after_the_tries_and_leaves_no_scratch_file()
    {
        File.WriteAllText(FilePath, "old");
        using var holder = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var pauses = 0;

        var failure = Record.Exception(() => AtomicFileWriter.Replace(_home.Path, FileName, Bytes("new"), _ => pauses++));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(AtomicFileWriter.Attempts - 1, pauses);
        Assert.Equal([FilePath], Directory.GetFileSystemEntries(_home.Path));
    }
}
