using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Processing.DirectPlay;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>Which devices the owner chose, what a file's row says about it, and the badge each chosen device gets.</summary>
public sealed class DirectPlayServiceTests : IDisposable
{
    private static readonly HashSet<string> Mkv = ["mkv"];

    private readonly StoreFixture _store = new();
    private readonly DirectPlayService _service;

    public DirectPlayServiceTests() =>
        _service = new DirectPlayService(new SuiteSettingsStore(new AuthStore(), new DataChangePublisher()));

    public void Dispose() => _store.Dispose();

    private static DeviceProfile Device(string id, string name) => new(
        id,
        name,
        "test",
        string.Empty,
        Mkv,
        new HashSet<string>(),
        new Dictionary<string, DeviceVideoLimits> { ["h264"] = new(1080, 8, null) },
        new Dictionary<string, DeviceVideoLimits>(),
        new HashSet<string> { "aac" },
        new HashSet<string>());

    private static readonly DeviceProfile[] Known = [Device("tv", "Living room TV"), Device("phone", "Phone")];

    private static ProcessingFileRecord Row(string path, string? audio = "aac,,ac3") =>
        new() { RelativePath = path, VideoCodec = "h264", VideoHeight = 1080, VideoBitDepth = 8, AudioCodecs = audio };

    [Fact]
    public async Task With_no_device_chosen_there_are_no_profiles()
    {
        var chosen = await _store.WithUnitOfWork(uow => _service.SelectedProfilesAsync(uow, Known));

        Assert.Empty(chosen);
    }

    [Fact]
    public async Task The_chosen_devices_come_back_in_the_lists_own_order_and_unknown_ids_are_dropped()
    {
        var saved = await _store.WithUnitOfWork(uow => _service.SaveSelectedDeviceIdsAsync(uow, Known, ["phone", "gone", "tv"]));

        var profiles = await _store.WithUnitOfWork(uow => _service.SelectedProfilesAsync(uow, Known), commit: false);

        Assert.Equal(["tv", "phone"], saved);
        Assert.Equal(["tv", "phone"], profiles.Select(profile => profile.Id));
    }

    [Fact]
    public void A_row_gives_the_facts_a_verdict_needs()
    {
        var facts = DirectPlayService.FactsForRow(Row("Film/film.MKV"));

        Assert.Equal(new[] { "aac", "ac3" }, facts.AudioCodecs);
        Assert.Equal(("mkv", "h264", 1080L, 8L), (facts.Container, facts.VideoCodec, facts.VideoHeight, facts.VideoBitDepth));
    }

    [Fact]
    public void A_row_whose_audio_was_not_measured_says_so_instead_of_saying_none()
    {
        Assert.Null(DirectPlayService.FactsForRow(Row("Film/film.mkv", audio: null)).AudioCodecs);
    }

    [Fact]
    public void A_row_gets_a_badge_for_each_chosen_device_and_none_when_none_are_chosen()
    {
        var badges = DirectPlayService.ForRow(Row("Film/film.mkv", audio: "aac"), Known);

        Assert.Equal(["tv", "phone"], badges.Select(badge => badge.DeviceId));
        Assert.All(badges, badge => Assert.Equal("yes", badge.Verdict));
        Assert.Empty(DirectPlayService.ForRow(Row("Film/film.mkv"), []));
    }

    [Fact]
    public void A_custom_device_list_is_noticed_only_when_the_file_is_in_the_weir_folder()
    {
        Assert.False(DirectPlayService.IsCustomised(null));
        Assert.False(DirectPlayService.IsCustomised(string.Empty));
        Assert.False(DirectPlayService.IsCustomised(_store.Home.Path));

        File.WriteAllText(_store.Home.Join(DirectPlayEvaluation.OverrideFileName), "[]");

        Assert.True(DirectPlayService.IsCustomised(_store.Home.Path));
    }
}
