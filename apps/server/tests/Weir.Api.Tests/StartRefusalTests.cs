using System.Text;
using Weir.Core.Configuration;
using Weir.Host;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Tests;

/// <summary>
/// A server that refuses to start for a reason starting again cannot change says why on standard error and in startup-error.txt, so the
/// Windows tray shows the reason and does not try again; and it exits whether or not anything reads standard error.
/// </summary>
public sealed class StartRefusalTests : IDisposable
{
    private readonly string _home = Path.Join(Path.GetTempPath(), "weir-refusal-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryStream _error = new();

    public StartRefusalTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        _error.Dispose();
        Directory.Delete(_home, recursive: true);
    }

    private RuntimeEnvironment Runtime(string? home = null) =>
        new(new Dictionary<string, string> { ["WEIR_HOME"] = home ?? _home }, OperatingSystem.IsWindows(), _home, _home);

    private string NotePath => Path.Join(_home, StartupNotes.ErrorFileName);

    [Fact]
    public void A_settings_error_is_printed_and_left_as_a_note_with_a_headline_of_its_own()
    {
        using var console = new NonBlockingConsole(_error, Encoding.UTF8);

        var exit = StartRefusal.Report(new WeirConfigurationException("WEIR_PORT must be a number."), Runtime(), console);

        Assert.Equal(1, exit);
        Assert.Equal("Weir's settings are not valid\nWEIR_PORT must be a number.", File.ReadAllText(NotePath));
        Assert.Contains("Weir cannot start: WEIR_PORT must be a number.", Encoding.UTF8.GetString(_error.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_this_build_cannot_use_is_left_as_a_note_too()
    {
        using var console = new NonBlockingConsole(_error, Encoding.UTF8);

        StartRefusal.Report(new DatabaseSchemaMismatchException("Database revision '0100_future' is not recognized."), Runtime(), console);

        Assert.StartsWith("Weir can't use its database\n", File.ReadAllText(NotePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_copy_before_updating_keeps_the_note_it_already_wrote()
    {
        StartupNotes.WriteError(_home, PreUpdateBackupException.Headline, "The drive is full.");
        using var console = new NonBlockingConsole(_error, Encoding.UTF8);

        StartRefusal.Report(new PreUpdateBackupException("The drive is full."), Runtime(), console);

        Assert.StartsWith(PreUpdateBackupException.Headline + "\n", File.ReadAllText(NotePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_that_is_not_starting_the_server_leaves_no_note()
    {
        using var console = new NonBlockingConsole(_error, Encoding.UTF8);

        StartRefusal.Report(new WeirConfigurationException("WEIR_PORT must be a number."), Runtime(), console, leaveNote: false);

        Assert.False(File.Exists(NotePath));
    }

    [Fact]
    public void A_data_folder_that_cannot_be_written_never_stops_the_refusal()
    {
        using var console = new NonBlockingConsole(_error, Encoding.UTF8);
        // Under a file, so that no folder can be made there.
        var blocker = Path.Join(_home, "blocker");
        File.WriteAllText(blocker, "not a folder");
        var nowhere = Path.Join(blocker, "data");

        var exit = StartRefusal.Report(new WeirConfigurationException("WEIR_PORT must be a number."), Runtime(nowhere), console);

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(nowhere));
    }

    [Fact]
    public async Task A_standard_error_nobody_reads_does_not_keep_the_server_from_exiting()
    {
        using var stuck = new NeverReturningStream();
        using var console = new NonBlockingConsole(stuck, Encoding.UTF8);

        var exit = await Task.Run(() => StartRefusal.Report(new WeirConfigurationException("WEIR_PORT must be a number."), Runtime(), console))
            .WaitAsync(TimeSpan.FromSeconds(30));

        // The note is there for the tray whether or not the words reached the console.
        Assert.Equal(1, exit);
        Assert.True(File.Exists(NotePath));
    }

    private sealed class NeverReturningStream : Stream
    {
        private readonly ManualResetEventSlim _release = new(false);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => _release.Wait();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _release.Set();
            base.Dispose(disposing);
        }
    }
}
