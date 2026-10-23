using System.Text;
using Weir.Core.Json;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The tray's files when they hold something that is not text Weir can read, and the flags Weir leaves for the tray.</summary>
public sealed class UpdateFilesUnreadableTests : IDisposable
{
    private static readonly byte[] NotUtf8 = [0x7B, 0xFF, 0xFD, 0xFC];

    private readonly StoreFixture _store = new(("WEIR_RUNTIME", "windows"));

    public void Dispose() => _store.Dispose();

    private string Home => _store.Options.WeirHome;

    private UpdateFiles Files() => new(_store.Options, _store.Clock);

    [Fact]
    public void A_heartbeat_that_is_not_text_is_no_tray()
    {
        File.WriteAllBytes(Path.Join(Home, UpdateFiles.TrayHeartbeatFileName), NotUtf8);

        Assert.False(Files().TrayIsRunning());
    }

    [Fact]
    public void Update_settings_that_are_not_text_are_notify_only_and_the_file_is_named()
    {
        var path = Path.Join(Home, UpdateFiles.SettingsFileName);
        File.WriteAllBytes(path, NotUtf8);
        var warned = new List<string>();

        var settings = Files().ReadSettings(warned.Add);

        Assert.Equal("NotifyOnly", ((WireString)settings["mode"]).Value);
        Assert.Equal([path], warned);
    }

    [Fact]
    public void Update_settings_that_start_with_a_byte_order_mark_are_read_as_written()
    {
        File.WriteAllBytes(Path.Join(Home, UpdateFiles.SettingsFileName), [.. Encoding.UTF8.GetPreamble(), .. """{"mode":"Auto"}"""u8.ToArray()]);
        var warned = new List<string>();

        var settings = Files().ReadSettings(warned.Add);

        Assert.Equal("Auto", ((WireString)settings["mode"]).Value);
        Assert.Empty(warned);
    }

    [Fact]
    public void A_state_the_tray_wrote_without_a_name_is_shown_as_the_default()
    {
        File.WriteAllText(Path.Join(Home, UpdateFiles.StateFileName), """{"state":5}""");

        var state = Files().ReadState();

        Assert.Equal("idle", ((WireString)state["state"]).Value);
    }

    [Fact]
    public void With_no_state_file_no_update_is_downloaded()
    {
        Assert.False(Files().HasDownloadedUpdate());
    }

    [Fact]
    public void Asking_the_tray_again_refreshes_the_flag_it_has_not_taken_up_instead_of_making_another()
    {
        Files().WriteCheckFlag();
        var path = Path.Join(Home, UpdateFiles.CheckFlagFileName);
        var old = DateTime.UtcNow.AddHours(-3);
        File.SetLastWriteTimeUtc(path, old);

        Files().WriteCheckFlag();

        Assert.True(File.GetLastWriteTimeUtc(path) > old.AddHours(1));
        Assert.Single(Directory.GetFiles(Home, UpdateFiles.CheckFlagFileName));
    }
}
