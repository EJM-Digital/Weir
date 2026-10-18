using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// A rule set saved through the API decides what a real pass keeps and strips: each test saves one, points the workflow at it, hands
/// the workflow a film generated with the real ffmpeg that carries every kind of track and tag involved, and reads what is left in the
/// copy Weir wrote with the real ffprobe. Skipped when ffmpeg and ffprobe are not on PATH.
/// </summary>
[ContractArea("processing")]
public sealed class RuleSetRealPassTests(RuleSetRealPassTests.World world) : IClassFixture<RuleSetRealPassTests.World>
{
    private static RealMedia.Film Everything(string title = "Tagged Title") => new(
        [
            new("eng", "Main", Channels: 2),
            new("ger", "Hauptfilm", Channels: 2),
            new("fre", "Principal", Channels: 2),
            new("eng", "Director's Commentary", Commentary: true, Channels: 2),
        ],
        [new("eng", Forced: true), new("eng", Default: true), new("fre"), new("ger")],
        Title: title,
        WithCover: true,
        WithAttachment: true,
        WithChapters: true);

    private static string Langs(PassResult pass, string kind) =>
        string.Join(",", pass.Tracks.Where(track => track.Kind == kind).Select(track => track.Language.Length == 0 ? "-" : track.Language));

    /// <summary>The audio languages of the copy, and when they are not the ones expected, what the metadata service was asked and what Weir logged about it.</summary>
    private async Task AssertAudioAsync(string expected, PassResult pass)
    {
        var actual = Langs(pass, "audio");
        Assert.True(expected == actual, $"Expected the audio {expected}, but the copy has {actual}. {await world.WhatTheMetadataServiceSawAsync()}");
    }

    private static IEnumerable<RealMedia.Described> Of(PassResult pass, string kind) => pass.Tracks.Where(track => track.Kind == kind);

    /// <summary>The copy Weir wrote: its tracks, and what else the film itself carries.</summary>
    internal sealed record PassResult(string Path, IReadOnlyList<RealMedia.Described> Tracks)
    {
        public Task<string> TitleAsync() => RealMedia.FilmTitleAsync(Path);

        public Task<int> ChaptersAsync() => RealMedia.ChapterCountAsync(Path);

        public Task<string> CommentAsync() => RealMedia.FilmCommentAsync(Path);
    }

    [RealFfmpegFact]
    public async Task Audio_languages_in_order_and_the_one_marked_default_follow_the_rule_set()
    {
        var film = await world.RunAsync(
            "Languages",
            new RealMedia.Film([new("jpn"), new("eng"), new("ger"), new("fre")], []),
            ("primary_audio_lang", "eng"),
            ("secondary_audio_lang", "ger"),
            ("tertiary_audio_lang", "fre"),
            ("audio_keep_mode", "per_language"),
            ("default_audio_slot", "tertiary"));

        var audio = Of(film, "audio").ToList();
        Assert.Equal(["eng", "ger", "fre"], audio.Select(track => track.Language));
        Assert.Equal(["fre"], audio.Where(track => track.Default).Select(track => track.Language));
    }

    [RealFfmpegFact]
    public async Task Commentary_is_dropped_only_when_the_rule_set_says_so()
    {
        var withIt = await world.RunAsync(
            "Commentary kept",
            new RealMedia.Film([new("fre", "Principal"), new("eng", "Director's Commentary", Commentary: true)], []),
            ("primary_audio_lang", "eng"));
        var without = await world.RunAsync(
            "Commentary removed",
            new RealMedia.Film([new("fre", "Principal"), new("eng", "Director's Commentary", Commentary: true)], []),
            ("primary_audio_lang", "eng"),
            ("remove_commentary", true));

        Assert.Equal("eng", Langs(withIt, "audio"));
        Assert.True(Of(withIt, "audio").Single().Commentary);
        Assert.Equal("fre", Langs(without, "audio"));
    }

    [RealFfmpegFact]
    public async Task The_audio_order_picks_the_track_it_ranks_first()
    {
        var film = new RealMedia.Film([new("eng", "Stereo", Channels: 2), new("eng", "Surround", Channels: 6)], []);
        var byDefault = await world.RunAsync("Order default", film, ("primary_audio_lang", "eng"));
        var flipped = await world.RunAsync(
            "Order flipped",
            film,
            ("primary_audio_lang", "eng"),
            ("audio_sorters_json", "[{\"field\": \"channels\", \"reversed\": true}]"));

        Assert.Equal([6], Of(byDefault, "audio").Select(track => track.Channels));
        Assert.Equal([2], Of(flipped, "audio").Select(track => track.Channels));
    }

    [RealFfmpegFact]
    public async Task Quality_across_all_languages_ignores_the_preferred_language_the_other_way_does_not()
    {
        var film = new RealMedia.Film([new("eng", "Stereo", Channels: 2), new("ger", "Surround", Channels: 6)], []);
        var preferred = await world.RunAsync("Preferred", film, ("primary_audio_lang", "eng"), ("audio_preference_mode", "preferred_langs_quality"));
        var quality = await world.RunAsync("Quality", film, ("primary_audio_lang", "eng"), ("audio_preference_mode", "quality_all_languages"));

        Assert.Equal("eng", Langs(preferred, "audio"));
        Assert.Equal("ger", Langs(quality, "audio"));
    }

    [RealFfmpegFact]
    public async Task The_original_language_the_metadata_service_names_decides_the_audio_kept()
    {
        var film = new RealMedia.Film([new("eng"), new("ger"), new("fre")], []);
        var plain = await world.RunNamedAsync("Nosferatu.1922.1080p", "Preferred only", film, ("primary_audio_lang", "eng"));
        var original = await world.RunNamedAsync("Nosferatu.1923.1080p", "Original language", film, ("primary_audio_lang", "eng"), ("keep_original_language", true));

        await AssertAudioAsync("eng", plain);
        await AssertAudioAsync("ger", original);
    }

    [RealFfmpegFact]
    public async Task Another_language_to_keep_is_chosen_when_the_original_is_not_in_the_file()
    {
        var film = new RealMedia.Film([new("eng"), new("fre")], []);
        var without = await world.RunNamedAsync("Nosferatu.1924.1080p", "Original missing", film, ("primary_audio_lang", "eng"), ("keep_original_language", true));
        var with = await world.RunNamedAsync(
            "Nosferatu.1925.1080p",
            "Original missing, other listed",
            film,
            ("primary_audio_lang", "eng"),
            ("keep_original_language", true),
            ("original_language_additional_csv", "fre"));

        await AssertAudioAsync("eng", without);
        await AssertAudioAsync("fre", with);
    }

    [RealFfmpegFact]
    public async Task An_untagged_track_is_taken_for_the_original_only_when_the_rule_set_says_so()
    {
        var film = new RealMedia.Film([new("eng"), new(string.Empty), new("fre")], []);
        var tagged = await world.RunNamedAsync("Nosferatu.1926.1080p", "Untagged ignored", film, ("primary_audio_lang", "eng"), ("keep_original_language", true));
        var untagged = await world.RunNamedAsync(
            "Nosferatu.1927.1080p",
            "Untagged is the original",
            film,
            ("primary_audio_lang", "eng"),
            ("keep_original_language", true),
            ("original_language_treat_empty_as_original", true));

        await AssertAudioAsync("eng", tagged);
        await AssertAudioAsync("-", untagged);
    }

    [RealFfmpegFact]
    public async Task Subtitles_keep_selected_keeps_only_the_listed_languages_and_the_forced_and_default_marks_the_rule_set_preserves()
    {
        var film = new RealMedia.Film([new("eng")], [new("fre", Forced: true, Default: true), new("eng"), new("ger")]);
        var both = await world.RunAsync("Both preserved", film, ("subtitle_mode", "keep_listed"), ("subtitle_langs_csv", " fre , "));
        var forcedOnly = await world.RunAsync(
            "Forced preserved", film, ("subtitle_mode", "keep_listed"), ("subtitle_langs_csv", "fre"), ("preserve_default_subs", false));
        var defaultOnly = await world.RunAsync(
            "Default preserved", film, ("subtitle_mode", "keep_listed"), ("subtitle_langs_csv", "fre"), ("preserve_forced_subs", false));
        var neither = await world.RunAsync(
            "Neither preserved",
            film,
            ("subtitle_mode", "keep_listed"),
            ("subtitle_langs_csv", "fre"),
            ("preserve_forced_subs", false),
            ("preserve_default_subs", false));

        static string Described(PassResult pass) => string.Join(
            ",", Of(pass, "subtitle").Select(track => track.Language + (track.Forced ? "+forced" : string.Empty) + (track.Default ? "+default" : string.Empty)));
        Assert.Equal("fre+forced+default", Described(both));
        Assert.Equal("fre+forced", Described(forcedOnly));
        Assert.Equal("fre+default", Described(defaultOnly));
        Assert.Equal("fre", Described(neither));
    }

    [RealFfmpegFact]
    public async Task Subtitles_keep_selected_with_two_languages_keeps_them_in_the_order_listed()
    {
        var film = new RealMedia.Film([new("eng")], [new("eng"), new("fre"), new("ger")]);

        var kept = await world.RunAsync("Two listed", film, ("subtitle_mode", "keep_listed"), ("subtitle_langs_csv", "ger, eng"));

        Assert.Equal("ger,eng", Langs(kept, "subtitle"));
    }

    [RealFfmpegFact]
    public async Task Subtitles_remove_all_takes_every_subtitle_out()
    {
        var film = await world.RunAsync("No subtitles", Everything(), ("subtitle_mode", "remove_all"), ("preserve_forced_subs", false), ("preserve_default_subs", false));

        Assert.Equal(string.Empty, Langs(film, "subtitle"));
    }

    [RealFfmpegFact]
    public async Task The_subtitle_order_puts_the_ranked_language_first()
    {
        var film = new RealMedia.Film([new("eng")], [new("eng"), new("fre"), new("ger")]);
        var inFileOrder = await world.RunAsync("Subtitles as they came", film, ("subtitle_mode", "keep_all"));
        var ranked = await world.RunAsync(
            "Subtitles ranked",
            film,
            ("subtitle_mode", "keep_all"),
            ("subtitle_sorters_json", "[{\"field\": \"language\", \"value\": \"ger\"}]"));

        Assert.Equal("eng,fre,ger", Langs(inFileOrder, "subtitle"));
        Assert.Equal("ger,eng,fre", Langs(ranked, "subtitle"));
    }

    [RealFfmpegFact]
    public async Task Each_metadata_removal_takes_out_only_what_it_names()
    {
        var kept = await world.RunAsync("Metadata kept", Everything());
        var images = await world.RunAsync("No images", Everything(), ("remove_images", true));
        var attachments = await world.RunAsync("No attachments", Everything(), ("remove_attachments", true));
        var title = await world.RunAsync("No title", Everything(), ("remove_title", true));
        var tags = await world.RunAsync("No language tags", Everything(), ("remove_language_tags", true));
        var other = await world.RunAsync("No other metadata", Everything(), ("remove_other_metadata", true));

        Assert.Equal(("Tagged Title", 2, 1), (await kept.TitleAsync(), Of(kept, "video").Count(), Of(kept, "attachment").Count()));
        Assert.Equal((1, 1), (Of(images, "video").Count(), Of(images, "attachment").Count()));
        Assert.Equal((2, 0), (Of(attachments, "video").Count(), Of(attachments, "attachment").Count()));
        Assert.Equal(string.Empty, await title.TitleAsync());
        Assert.Equal("eng", Langs(title, "audio"));
        Assert.DoesNotContain(tags.Tracks.Where(track => track.Kind is "audio" or "subtitle"), track => track.Language.Length > 0);
        Assert.Equal("Encoded for a contract test", await kept.CommentAsync());
        Assert.Equal("Encoded for a contract test", await title.CommentAsync());
        Assert.Equal(string.Empty, await other.CommentAsync());
        // Clearing the other metadata clears every tag of the file, the title included, and leaves each track's language alone.
        Assert.Equal(string.Empty, await other.TitleAsync());
        Assert.Equal(("eng", "eng,eng,fre,ger"), (Langs(other, "audio"), Langs(other, "subtitle")));
    }

    [RealFfmpegFact]
    public async Task Stripping_all_metadata_leaves_the_picture_one_audio_track_and_nothing_that_describes_the_file()
    {
        var film = await world.RunAsync(
            "Stripped",
            Everything(),
            ("remove_images", true),
            ("remove_attachments", true),
            ("remove_title", true),
            ("remove_language_tags", true),
            ("remove_other_metadata", true),
            ("remove_chapters", true));

        Assert.Equal(["video", "audio", "subtitle", "subtitle", "subtitle", "subtitle"], film.Tracks.Select(track => track.Kind));
        Assert.Equal(string.Empty, await film.TitleAsync());
        Assert.Equal(0, await film.ChaptersAsync());
    }

    /// <summary>One workflow on a server with the real tools and a metadata service that knows one title.</summary>
    public sealed class World : IAsyncLifetime, IDisposable
    {
        private readonly FakeGateway _gateway = new();
        private Scenario _scenario = null!;
        private JsonObject _library = null!;
        private int _runs;

        public async Task InitializeAsync()
        {
            if (!RealFfmpeg.IsAvailable)
            {
                return;
            }

            _gateway.Knows("nosferatu", "nosferatu.jpg", tmdbId: 653, originalLanguage: "de");
            _scenario = await Scenario.StartWithRealToolsAsync([.. _gateway.Env.Select(setting => (setting.Key, setting.Value))]);
            _library = await _scenario.CreateLibraryAsync();
        }

        public async Task DisposeAsync()
        {
            if (RealFfmpeg.IsAvailable)
            {
                await _scenario.DisposeAsync();
            }
        }

        public void Dispose() => _gateway.Dispose();

        /// <summary>What a pass that kept the wrong language needs to be explained: the searches the service answered and the warnings Weir logged.</summary>
        internal async Task<string> WhatTheMetadataServiceSawAsync()
        {
            var log = await _scenario.Admin.GetAsync($"{WeirClient.Api}/system/log", ("level", "warning,error"), ("limit", 20));
            var logged = log.Fields["items"]!.AsArray().Select(row => (string)row!["title"]!);
            return $"The service answered {_gateway.Searches().Count} searches ({string.Join(", ", _gateway.Searches().Select(search => search.Query.GetValueOrDefault("query")?[0]))}). Weir logged: {string.Join(" | ", logged)}.";
        }

        /// <summary>Saves a rule set with these settings, points the workflow at it, passes a film through it and returns the tracks of the copy written.</summary>
        internal async Task<PassResult> RunAsync(string name, RealMedia.Film film, params (string Setting, JsonNode Value)[] settings) =>
            await RunNamedAsync(null, name, film, settings);

        /// <summary>The same, for a film in a folder of this name, which is what the metadata service is asked about.</summary>
        internal async Task<PassResult> RunNamedAsync(string? release, string name, RealMedia.Film film, params (string Setting, JsonNode Value)[] settings)
        {
            var body = new JsonObject { ["name"] = $"{name} {++_runs}" };
            foreach (var (setting, value) in settings)
            {
                body[setting] = value.DeepClone();
            }

            var saved = await _scenario.Admin.PostWithCsrfAsync($"{WeirClient.Api}/processing/rule-sets", body);
            Assert.True(saved.Status == HttpStatusCode.Created, saved.ToString());
            await _scenario.UpdateLibraryAsync(_library, ("rule_set_id", (long)saved.Fields["id"]!));

            release ??= $"Film.{_runs}";
            var file = $"{release}.mkv";
            var source = Path.Combine(_scenario.Folders.Watched, release, file);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await RealMedia.GenerateFilmAsync(source, film);
            await _scenario.EnqueueFilePassAsync($"{release}/{file}", _library);
            var output = Path.Combine(_scenario.Folders.Output, release, file);
            await _scenario.WaitWhileWorkedOnAsync(async () => File.Exists(output) ? output : null, $"Weir to write its copy of {file}");
            return new PassResult(output, await RealMedia.DescribeAsync(output));
        }

    }
}
