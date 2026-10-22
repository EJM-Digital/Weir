using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Core.Library;
using Weir.Core.Processing;
using Weir.Core.Rules;

namespace Weir.Core.Tests.LibraryMode;

/// <summary>Library mode's settings, schedule, swap wording and manager data at their edges: nothing given, blank entries, and values of the wrong kind.</summary>
public sealed class LibraryModeEdgeTests
{
    private static readonly DateTimeOffset Morning = new(2026, 9, 23, 8, 30, 0, TimeSpan.Zero);

    private static ProcessingLibraryRecord Library() => new()
    {
        Name = "Movies",
        WatchedFolder = "/data/watched",
        WorkFolder = string.Empty,
        OutputFolder = "/data/output",
    };

    private static ProcessingRulesConfig Rules() => new()
    {
        PrimaryAudioLang = "eng",
        SecondaryAudioLang = string.Empty,
        TertiaryAudioLang = string.Empty,
        DefaultAudioSlot = "primary",
        RemoveCommentary = false,
        SubtitleMode = "keep_all",
        SubtitleLangs = [],
        PreserveForcedSubs = true,
        PreserveDefaultSubs = true,
        AudioPreferenceMode = "preferred_langs_quality",
        AudioSortersJson = string.Empty,
        Metadata = new MetadataRules(),
        PreferredAudioIndices = [],
        OriginalLanguageNote = string.Empty,
    };

    [Fact]
    public void A_settings_row_with_nothing_in_it_has_the_documented_defaults()
    {
        var settings = LibrarySettings.FromPayload((WireObject)WireJsonParser.Parse("{}"));

        Assert.Empty(settings.Folders);
        Assert.Equal(new LibrarySettings(settings.Folders, false), settings);
        Assert.True(settings.SkipIfManagerWouldRedownload);
    }

    [Fact]
    public void Every_setting_in_a_row_is_read()
    {
        var settings = LibrarySettings.FromPayload((WireObject)WireJsonParser.Parse(
            """
            {"library_folders": ["/a", "", 5], "library_schedule_enabled": true, "clean_hardlinked_files": true,
             "skip_if_manager_would_redownload": false, "keep_original_after_clean": true, "originals_folder": "/o", "library_rule_set_id": 4}
            """));

        Assert.Equal(["/a"], settings.Folders);
        Assert.Equal(new LibrarySettings(settings.Folders, true, true, false, true, "/o", 4), settings);
    }

    [Fact]
    public void A_blank_or_missing_library_folder_is_dropped_and_a_repeat_is_kept_once()
    {
        var folders = LibraryFolderRules.Validate([null!, "  ", "/data/a", "/DATA/a/"], Library());

        Assert.Equal(["/data/a"], folders);
    }

    [Fact]
    public void A_missing_originals_folder_is_the_default_one()
    {
        Assert.Equal(string.Empty, LibraryFolderRules.ValidateOriginalsFolder(null, [], Library()));
    }

    [Fact]
    public void A_blank_scan_folder_is_ignored_when_the_originals_folder_is_checked_against_them()
    {
        Assert.Equal("/data/originals", LibraryFolderRules.ValidateOriginalsFolder("/data/originals", ["", "/data/library"], Library()));
    }

    [Fact]
    public void An_originals_folder_that_sits_inside_a_library_folder_without_a_leading_dot_is_refused()
    {
        var exception = Assert.Throws<LibraryModeException>(
            () => LibraryFolderRules.ValidateOriginalsFolder("/data/library/originals", ["/data/library"], Library()));

        Assert.Contains("start with a dot", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scheduled_run_is_looked_for_a_little_before_the_day_is_up_when_the_window_is_shut_then()
    {
        var grid = Weir.Core.Jobs.ScheduleGrid.FromDaysAndTimes("Mon,Tue,Wed,Thu,Fri,Sat,Sun", "08:00", "10:00");
        var window = new Weir.Core.Jobs.LibraryAdmissionSnapshot(1, true, true, grid, false, null, null, null, 0);
        var lastRun = new DateTimeOffset(2026, 9, 22, 10, 30, 0, TimeSpan.Zero);

        Assert.Equal(Morning, LibraryModeSchedule.NextRunAt(lastRun, window, "UTC", Morning));
    }

    [Fact]
    public void A_removed_track_of_a_kind_that_does_not_exist_is_refused()
    {
        var removed = new RemovedTrackRecord
        {
            Language = "eng",
            Type = (RemovedTrackType)99,
            Codec = "ac3",
            Variant = string.Empty,
            Reason = "test",
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => RemovedTrackDiff.WouldNowBeKept(Rules(), removed));
    }

    [Fact]
    public void Every_language_the_two_managers_name_has_a_code_and_the_ones_that_name_no_language_have_none()
    {
        foreach (var kind in new[] { "sonarr", "radarr" })
        {
            for (var id = -2; id <= 120; id++)
            {
                _ = ArrLanguageCatalog.CanonicalCodeFor(kind, id);
            }

            Assert.Null(ArrLanguageCatalog.CanonicalCodeFor(kind, 0));
            Assert.NotNull(ArrLanguageCatalog.CanonicalCodeFor(kind, 1));
        }

        Assert.Null(ArrLanguageCatalog.CanonicalCodeFor("lidarr", 1));
    }

    [Fact]
    public void A_file_with_no_video_and_an_audio_track_that_gives_its_numbers_as_text_is_still_described()
    {
        var facts = LibraryFileFactsReader.Derive("""{"streams":[{"index":0,"codec_type":"audio","codec_name":"ac3","channels":"six","channel_layout":5}]}""");

        Assert.Equal(LibraryFileFacts.Unknown, facts.VideoCodec);
        Assert.Equal(1, facts.AudioTrackCount);
    }

    [Fact]
    public void A_manager_file_with_parts_missing_is_read_as_far_as_it_goes()
    {
        var snapshot = RedownloadRiskDialect.ParseFileFormatSnapshot(
            WireJsonParser.Parse(
                """
                {"customFormats":[{"specifications":[
                  {},
                  {"implementation":"LanguageSpecification"},
                  {"implementation":"LanguageSpecification","negate":true,"fields":[{"name":"value","value":"x"},{"name":"exceptLanguage","value":false}]}
                ]}]}
                """),
            "radarr");

        var format = Assert.Single(snapshot.CustomFormats);
        Assert.Equal(0, format.Id);
        Assert.Equal(string.Empty, format.Name);
        Assert.Equal(3, format.Specifications.Count);
        Assert.Equal(0, snapshot.CustomFormatScore);
        Assert.All(format.Specifications, specification => Assert.Null(specification.LanguageCode));
    }

    [Fact]
    public void A_quality_profile_item_with_no_format_scores_nothing()
    {
        var profile = RedownloadRiskDialect.ParseQualityProfile(WireJsonParser.Parse("""{"formatItems":[{"score":5},{"format":3}]}"""));

        Assert.Equal(new Dictionary<long, int> { [3] = 0 }, profile.FormatScores);
        Assert.False(profile.UpgradeAllowed);
        Assert.Equal(0, profile.CutoffFormatScore);
    }

    [Fact]
    public void A_format_that_would_stop_matching_with_a_negative_score_says_so_without_a_plus_sign()
    {
        var warning = new RedownloadRiskWarning("Sonarr", "Show", "Dubbed", -25);

        Assert.Equal("Sonarr may download Show again: its 'Dubbed' format (-25) would no longer match.", warning.Message);
    }

    [Fact]
    public void A_file_that_neither_seeds_nor_risks_a_redownload_has_no_problem_and_a_risk_alone_is_a_redownload_problem()
    {
        var clean = new LibraryFilePreflightResult { FilePath = "/a.mkv" };
        var risky = clean with { RedownloadRisk = true };
        var seeding = risky with { Seeding = true };

        Assert.Null(clean.ProblemKind);
        Assert.Equal(LibraryProblemKind.ManagerRedownload, risky.ProblemKind);
        Assert.Equal(LibraryProblemKind.Seeding, seeding.ProblemKind);
    }

    [Theory]
    [InlineData(null, "The cleaned copy did not pass Weir's output checks, so the original was kept.")]
    [InlineData("  ", "The cleaned copy did not pass Weir's output checks, so the original was kept.")]
    [InlineData("no video", "The cleaned copy did not pass Weir's output checks, so the original was kept: no video")]
    public void The_message_for_a_cleaned_copy_that_failed_its_checks_names_the_problem_only_when_there_is_one(string? problem, string expected)
    {
        Assert.Equal(expected, SafeSwapRules.ValidationFailedMessage(problem));
    }

    [Theory]
    [InlineData(500, "500 bytes")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(-5, "0 bytes")]
    public void Sizes_are_written_in_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, SafeSwapRules.FormatBytes(bytes));
    }

    [Fact]
    public void A_path_that_ends_in_a_separator_has_no_file_to_put_a_marker_on()
    {
        Assert.Throws<ArgumentException>(() => SafeSwapRules.TempPath("/media/"));
    }

    [Fact]
    public void A_blank_library_folder_contains_nothing()
    {
        Assert.Equal("/media", OriginalsPathPlanner.ContainingFolder(["  ", "/media"], "/media/film.mkv"));
    }
}
