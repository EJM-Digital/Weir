using System.Text.Json;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The tray is the component that actually installs an update, so what it reads out of
/// update-settings.json decides whether software lands on a machine unattended. A damaged
/// file must never read as Auto.
/// </summary>
public sealed class UpdateSettingsTests : IDisposable
{
    // Load logs to tray-host.log on the failure path, so the temp folder is also WEIR_HOME: a test can never
    // append to a real install's log.
    private readonly TempDirectory _temp = TempDirectory.AsWeirHome();

    public void Dispose() => _temp.Dispose();

    private string Home => _temp.Path;

    private string SettingsPath => Path.Combine(Home, "update-settings.json");

    [Fact]
    public void No_file_is_the_shipped_default()
    {
        var got = UpdateSettings.Load(Home);

        Assert.Equal(UpdateMode.Auto, got.Mode);
        Assert.True(got.CheckOnStartup);
        Assert.Equal(60, got.CheckIntervalMinutes);
    }

    [Fact]
    public void A_saved_choice_round_trips()
    {
        new UpdateSettings
        {
            Mode = UpdateMode.DownloadOnly,
            CheckOnStartup = false,
            CheckIntervalMinutes = 240,
        }.Save(Home);

        var got = UpdateSettings.Load(Home);

        Assert.Equal(UpdateMode.DownloadOnly, got.Mode);
        Assert.False(got.CheckOnStartup);
        Assert.Equal(240, got.CheckIntervalMinutes);
    }

    /// <summary>The same text the server writes when the Settings page saves (UpdateApiTests.A_saved_choice_reaches_the_tray_in_the_file_it_follows).</summary>
    [Theory]
    [InlineData("Auto", true, 60, true, "{\n  \"mode\": \"Auto\",\n  \"checkOnStartup\": true,\n  \"checkIntervalMinutes\": 60,\n  \"checkForUpdates\": true\n}")]
    [InlineData("DownloadOnly", false, 240, false, "{\n  \"mode\": \"DownloadOnly\",\n  \"checkOnStartup\": false,\n  \"checkIntervalMinutes\": 240,\n  \"checkForUpdates\": false\n}")]
    [InlineData("NotifyOnly", true, 10080, true, "{\n  \"mode\": \"NotifyOnly\",\n  \"checkOnStartup\": true,\n  \"checkIntervalMinutes\": 10080,\n  \"checkForUpdates\": true\n}")]
    public void A_file_the_settings_page_wrote_is_followed_as_saved(string mode, bool checkOnStartup, int minutes, bool checkForUpdates, string text)
    {
        File.WriteAllText(SettingsPath, text.Replace("\n", "\r\n", StringComparison.Ordinal));

        var got = UpdateSettings.Load(Home);

        Assert.Equal(Enum.Parse<UpdateMode>(mode), got.Mode);
        Assert.Equal(checkOnStartup, got.CheckOnStartup);
        Assert.Equal(minutes, got.CheckIntervalMinutes);
        Assert.Equal(checkForUpdates, got.CheckForUpdates);
    }

    [Fact]
    public void No_file_checks_for_nothing_because_nobody_has_chosen_to()
    {
        Assert.False(UpdateSettings.Load(Home).CheckForUpdates);
    }

    [Fact]
    public void A_file_saved_before_the_choice_existed_keeps_checking()
    {
        File.WriteAllText(SettingsPath, "{\n  \"mode\": \"DownloadOnly\",\n  \"checkOnStartup\": false,\n  \"checkIntervalMinutes\": 240\n}");

        var got = UpdateSettings.Load(Home);

        Assert.True(got.CheckForUpdates);
        Assert.Equal(UpdateMode.DownloadOnly, got.Mode);
    }

    [Fact]
    public void Switching_checking_off_is_kept()
    {
        new UpdateSettings { CheckForUpdates = false }.Save(Home);

        Assert.False(UpdateSettings.Load(Home).CheckForUpdates);
        Assert.Contains("\"checkForUpdates\": false", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"mode\": \"Notify")]
    [InlineData("null")]
    [InlineData("{\"mode\": \"InstallEverythingNow\"}")]
    public void A_file_that_cannot_be_read_checks_for_nothing_as_well_as_installing_nothing(string text)
    {
        File.WriteAllText(SettingsPath, text);

        var got = UpdateSettings.Load(Home);

        Assert.False(got.CheckForUpdates);
        Assert.Equal(UpdateMode.NotifyOnly, got.Mode);
    }

    [Fact]
    public void The_first_start_of_a_new_install_saves_checking_as_off_and_says_so()
    {
        UpdateSettings.SettleFirstRun(Home, _ => null);

        var got = UpdateSettings.Load(Home);

        Assert.True(File.Exists(SettingsPath));
        Assert.False(got.CheckForUpdates);
        Assert.Equal(UpdateMode.Auto, got.Mode);
        Assert.Contains("This is a new install, so update checking starts off.", LogFile.ReadAllText(Path.Combine(Home, "tray-host.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_start_of_an_upgrade_keeps_checking_because_Weir_has_run_here_before()
    {
        Directory.CreateDirectory(Path.Combine(Home, "data"));
        File.WriteAllText(Path.Combine(Home, "data", "weir.sqlite3"), "a database");

        UpdateSettings.SettleFirstRun(Home, _ => null);

        Assert.True(UpdateSettings.Load(Home).CheckForUpdates);
        Assert.Contains("Weir has run here before, so update checking stays on.", LogFile.ReadAllText(Path.Combine(Home, "tray-host.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void An_upgrade_with_its_database_somewhere_else_is_still_an_upgrade()
    {
        var elsewhere = Path.Combine(Home, "elsewhere", "weir-data.db");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "a database");

        UpdateSettings.SettleFirstRun(Home, name => name == "WEIR_DB_PATH" ? elsewhere : null);

        Assert.True(UpdateSettings.Load(Home).CheckForUpdates);
    }

    [Fact]
    public void A_database_path_relative_to_the_home_is_looked_for_in_the_home()
    {
        Directory.CreateDirectory(Path.Combine(Home, "db"));
        File.WriteAllText(Path.Combine(Home, "db", "weir.db"), "a database");

        UpdateSettings.SettleFirstRun(Home, name => name == "WEIR_DB_PATH" ? Path.Combine("db", "weir.db") : null);

        Assert.True(UpdateSettings.Load(Home).CheckForUpdates);
    }

    [Fact]
    public void A_start_that_finds_the_file_leaves_it_alone()
    {
        new UpdateSettings { Mode = UpdateMode.DownloadOnly, CheckForUpdates = false }.Save(Home);
        Directory.CreateDirectory(Path.Combine(Home, "data"));
        File.WriteAllText(Path.Combine(Home, "data", "weir.sqlite3"), "a database");

        UpdateSettings.SettleFirstRun(Home, _ => null);

        var got = UpdateSettings.Load(Home);
        Assert.False(got.CheckForUpdates);
        Assert.Equal(UpdateMode.DownloadOnly, got.Mode);
    }

    [Fact]
    public void A_first_start_that_cannot_save_the_file_says_so_and_the_tray_checks_for_nothing()
    {
        var blocked = Path.Combine(Home, "not-a-folder");
        File.WriteAllText(blocked, "a file where the folder should be");

        UpdateSettings.SettleFirstRun(blocked, _ => null);

        Assert.False(UpdateSettings.Load(blocked).CheckForUpdates);
    }

    [Fact]
    public void A_truncated_file_falls_back_to_notify_only_not_to_auto()
    {
        // The operator chose something. Auto is the only mode that installs with nobody
        // watching, so it is the one guess that can act against that choice.
        new UpdateSettings { Mode = UpdateMode.NotifyOnly }.Save(Home);
        File.WriteAllText(SettingsPath, "{\"mode\": \"Notify");

        Assert.Equal(UpdateMode.NotifyOnly, UpdateSettings.Load(Home).Mode);
    }

    [Fact]
    public void An_unknown_mode_is_rejected_rather_than_defaulting_to_auto()
    {
        File.WriteAllText(SettingsPath, "{\"mode\": \"InstallEverythingNow\"}");

        Assert.Equal(UpdateMode.NotifyOnly, UpdateSettings.Load(Home).Mode);
    }

    [Fact]
    public void An_empty_file_falls_back_to_notify_only()
    {
        // Deserialize returns null for a bare "null" document rather than throwing, so this
        // case needs its own check to avoid reading as the default, Auto.
        File.WriteAllText(SettingsPath, "null");

        Assert.Equal(UpdateMode.NotifyOnly, UpdateSettings.Load(Home).Mode);
    }

    [Fact]
    public void A_file_that_stays_busy_installs_and_checks_nothing_for_now_and_the_log_says_so()
    {
        File.WriteAllText(SettingsPath, "{ \"mode\": \"Auto\" }");
        using var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var got = UpdateSettings.Load(Home);

        Assert.Equal(UpdateMode.NotifyOnly, got.Mode);
        Assert.False(got.CheckForUpdates);
        Assert.Contains("update-settings.json could not be read just now", LogFile.ReadAllText(Path.Combine(Home, "tray-host.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_path_says_why_in_the_tray_log()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        UpdateSettings.Load(Home);

        var log = LogFile.ReadAllText(Path.Combine(Home, "tray-host.log"));
        Assert.Contains("update-settings.json could not be read", log);
        Assert.Contains("NotifyOnly", log);
    }

    [Fact]
    public void A_stale_scratch_file_from_another_writer_is_not_touched()
    {
        // The server writes this same file. A shared scratch name would let one writer rename the
        // other's half-written file into place and report success, so each write uses its own name
        // and a scratch file at the obvious shared name survives untouched.
        var foreign = SettingsPath + ".tmp";
        File.WriteAllText(foreign, "{\"mode\": \"Auto\"}");

        new UpdateSettings { Mode = UpdateMode.NotifyOnly }.Save(Home);

        Assert.Equal(UpdateMode.NotifyOnly, UpdateSettings.Load(Home).Mode);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void The_file_is_replaced_whole_and_leaves_no_scratch_file()
    {
        new UpdateSettings { Mode = UpdateMode.DownloadOnly, CheckIntervalMinutes = 10080 }.Save(Home);
        new UpdateSettings { Mode = UpdateMode.Auto, CheckIntervalMinutes = 1 }.Save(Home);

        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal("Auto", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("checkIntervalMinutes").GetInt32());
        Assert.Empty(Directory.GetFiles(Home, "*.tmp"));
    }
}
