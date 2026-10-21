using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>The marker a server leaves to say it can make the copy before an update, and the check for a server still running.</summary>
public sealed class UpdateBackupReadyTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    public void Dispose() => _home.Dispose();

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{\"pid\": 12}")]
    [InlineData("{\"started_at\": \"2026-10-01T10:00:00Z\"}")]
    public void A_marker_that_names_no_server_names_none(string text)
    {
        Write(text);

        Assert.Equal((false, (RunningServer?)null), UpdateBackupReady.Look(_home.Path));
        Assert.Null(UpdateBackupReady.Read(_home.Path));
    }

    [Fact]
    public void A_marker_names_the_server_that_wrote_it()
    {
        Write("{\"pid\": 4321, \"started_at\": \"2026-10-01T10:00:00Z\"}");

        var server = UpdateBackupReady.Read(_home.Path);

        Assert.Equal(4321, server!.Value.ProcessId);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), server.Value.StartedUtc);
    }

    [Fact]
    public void A_server_is_found_while_the_process_the_marker_names_is_the_same_one_and_running()
    {
        using var self = Process.GetCurrentProcess();
        Write(Marker(self.Id, self.StartTime.ToUniversalTime()));

        var live = UpdateBackupReady.FindLiveServer(_home.Path);

        Assert.Equal(self.Id, live!.Value.ProcessId);
    }

    [Fact]
    public void No_server_is_found_without_a_marker_for_a_process_that_has_gone_or_for_one_that_only_reuses_the_id()
    {
        Assert.Null(UpdateBackupReady.FindLiveServer(_home.Path));

        Write(Marker(int.MaxValue, DateTime.UtcNow));
        Assert.Null(UpdateBackupReady.FindLiveServer(_home.Path));

        Write(Marker(Environment.ProcessId, DateTime.UtcNow.AddDays(-30)));
        Assert.Null(UpdateBackupReady.FindLiveServer(_home.Path));
    }

    private static string Marker(int pid, DateTime startedUtc) =>
        $"{{\"pid\": {pid.ToString(CultureInfo.InvariantCulture)}, \"started_at\": \"{startedUtc:O}\"}}";

    private void Write(string text) => File.WriteAllText(Path.Combine(_home.Path, UpdateBackupReady.FileName), text);
}
