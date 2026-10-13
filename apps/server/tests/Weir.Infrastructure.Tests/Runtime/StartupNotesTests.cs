using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>Why a server could not start, for the tray (#951). How far a start got is StartupProgressTests.</summary>
public sealed class StartupNotesTests : IDisposable
{
    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void An_error_is_a_headline_then_the_whole_sentence()
    {
        StartupNotes.WriteError(_home.Path, "Couldn't save a copy", "Weir couldn't save a copy of its data: the drive is full.");

        Assert.Equal(
            "Couldn't save a copy\nWeir couldn't save a copy of its data: the drive is full.",
            File.ReadAllText(_home.Join(StartupNotes.ErrorFileName)));
    }

    [Fact]
    public void A_start_removes_what_the_last_one_left()
    {
        StartupNotes.WriteError(_home.Path, "headline", "sentence");

        StartupNotes.ClearStale(_home.Path);

        Assert.Empty(Directory.EnumerateFiles(_home.Path));
    }

    [Fact]
    public void A_note_that_cannot_be_written_never_stops_the_start()
    {
        var missing = _home.Join("not", "there");

        StartupNotes.WriteError(missing, "headline", "sentence");
        StartupNotes.ClearStale(missing);

        Assert.False(Directory.Exists(missing));
    }
}
