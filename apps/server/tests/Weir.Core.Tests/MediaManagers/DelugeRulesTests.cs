using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="DelugeRules"/> on what a real Deluge Web UI answered to <c>core.get_config</c>, <c>label.get_labels</c> and
/// <c>label.get_options</c> (<c>fixtures/recorded/deluge</c>). A test that needs a case no run produced says which recorded reply it started
/// from and which member it changed.
/// </summary>
public sealed class DelugeRulesTests
{
    private static WireValue Reply(ArrRecording recording, string method, int index = 0) =>
        WireJsonParser.Parse(recording.Exchanges.Where(exchange => (string?)exchange.Request.Body?["method"] == method).ElementAt(index).Reply.Text);

    private static WireValue Core(string state) => Reply(ArrRecording.Load("deluge", state), "core.get_config");

    private static List<(string Label, WireValue? Options)> Labels(ArrRecording recording)
    {
        var names = DelugeRules.LabelNames(Reply(recording, "label.get_labels"));
        return [.. names.Select((name, index) => (name, (WireValue?)Reply(recording, "label.get_options", index)))];
    }

    [Fact]
    public void Completed_downloads_end_up_in_the_move_completed_folder_when_it_is_switched_on()
    {
        var recording = ArrRecording.Load("deluge", "folders");

        var folders = DelugeRules.Parse(Core("folders"), Labels(recording));

        Assert.Equal("/downloads/complete", folders.CompletedFolder);
    }

    [Fact]
    public void Completed_downloads_stay_in_the_download_folder_when_moving_them_is_switched_off()
    {
        var folders = DelugeRules.Parse(Core("folders-move-completed-off"), []);

        Assert.Equal("/downloads/incomplete", folders.CompletedFolder);
    }

    [Fact]
    public void A_label_has_a_folder_of_its_own_only_when_it_applies_and_uses_a_move_completed_folder()
    {
        var recording = ArrRecording.Load("deluge", "folders");

        var folders = DelugeRules.Parse(Core("folders"), Labels(recording));

        Assert.Equal(["idle-recorder", "plain-recorder", "tv-recorder"], Labels(recording).Select(label => label.Label));
        Assert.Equal([new DownloadClientCategoryFolder("tv-recorder", "/downloads/complete/tv")], folders.CategoryFolders);
    }

    [Fact]
    public void A_label_whose_options_were_not_answered_has_no_folder()
    {
        var recording = ArrRecording.Load("deluge", "folders");
        var unanswered = Labels(recording).Select(label => (label.Label, Options: (WireValue?)null)).ToList();

        var folders = DelugeRules.Parse(Core("folders"), unanswered);

        Assert.Empty(folders.CategoryFolders);
    }

    [Fact]
    public void The_label_plugin_being_off_means_no_labels_not_a_failed_read()
    {
        var recording = ArrRecording.Load("deluge", "folders-stock");

        var names = DelugeRules.LabelNames(Reply(recording, "label.get_labels"));
        var folders = DelugeRules.Parse(Core("folders-stock"), []);

        Assert.Empty(names);
        Assert.Equal("/downloads", folders.CompletedFolder);
        Assert.Empty(folders.CategoryFolders);
    }

    [Fact]
    public void A_core_config_that_answered_an_error_has_no_completed_folder()
    {
        var recording = ArrRecording.Load("deluge", "folders-stock");

        var folders = DelugeRules.Parse(Reply(recording, "label.get_labels"), []);

        Assert.Null(folders.CompletedFolder);
    }
}
