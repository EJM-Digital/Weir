using Microsoft.Data.Sqlite;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>Rule-set CRUD, and the default-profile bookkeeping a library falls back to when it has none.</summary>
public sealed partial class LibraryStore
{
    private const string RuleSetColumns =
        "id, name, primary_audio_lang, secondary_audio_lang, tertiary_audio_lang, default_audio_slot, remove_commentary, " +
        "subtitle_mode, subtitle_langs_csv, preserve_forced_subs, preserve_default_subs, audio_preference_mode, audio_sorters_json, " +
        "subtitle_sorters_json, keep_original_language, original_language_additional_csv, original_language_keep_only_first, " +
        "original_language_first_if_none, original_language_treat_empty_as_original, remove_images, remove_attachments, " +
        "remove_title, remove_language_tags, remove_other_metadata, remove_hearing_impaired_subs, audio_keep_mode, " +
        "subtitle_max_per_language, subtitle_quality_strategy, standardize_track_names, track_name_template, " +
        "track_name_override_forced, track_name_override_hearing_impaired, track_name_override_commentary, " +
        "track_name_override_audio_description, clear_video_track_names, remove_chapters, created_at, updated_at";

    public async Task<List<ProcessingRuleSetRecord>> ListRuleSetsAsync(UnitOfWork uow) =>
        await uow.QueryAsync($"SELECT {RuleSetColumns} FROM rule_sets ORDER BY id", ReadRuleSet).ConfigureAwait(false);

    public Task<ProcessingRuleSetRecord?> GetRuleSetAsync(UnitOfWork uow, long id) =>
        uow.QuerySingleAsync($"SELECT {RuleSetColumns} FROM rule_sets WHERE id = @id", ReadRuleSet, ("@id", id));

    public Task<ProcessingRuleSetRecord?> GetRuleSetByNameAsync(UnitOfWork uow, string name) =>
        uow.QuerySingleAsync($"SELECT {RuleSetColumns} FROM rule_sets WHERE name = @name", ReadRuleSet, ("@name", name));

    /// <summary>The profile a library of <paramref name="mediaScope"/> gets when none is chosen: "Movies default" or "TV default".</summary>
    public static string DefaultProfileName(string mediaScope) =>
        ProcessingMediaScopes.Normalize(mediaScope) == ProcessingMediaScopes.Tv ? "TV default" : "Movies default";

    /// <summary>
    /// The profile a library of <paramref name="mediaScope"/> should have when it has none, matching what Weir did for it
    /// until 3.2: new downloads followed the first library of the kind's profile when it had one, and otherwise Weir's
    /// built-in rules, which become the "Movies default" or "TV default" profile (made once, then reused).
    /// </summary>
    public async Task<long> DefaultProfileIdAsync(UnitOfWork uow, string mediaScope)
    {
        if (await SeededForScopeAsync(uow, mediaScope).ConfigureAwait(false) is { RuleSetId: { } followed }
            && await GetRuleSetAsync(uow, followed).ConfigureAwait(false) is not null)
        {
            return followed;
        }

        var name = DefaultProfileName(mediaScope);
        if (await GetRuleSetByNameAsync(uow, name).ConfigureAwait(false) is { } existing)
        {
            return existing.Id;
        }

        await InsertRuleSetAsync(uow, RuleSetConversion.BuiltInDefaults(name)).ConfigureAwait(false);
        return (await GetRuleSetByNameAsync(uow, name).ConfigureAwait(false) ?? throw new InvalidOperationException("Default profile insert race.")).Id;
    }

    /// <summary>
    /// Every library has a profile: gives each one without a profile the one it was in effect using. Libraries are
    /// handled in display order, so each gets the same profile its rules already came from. Returns how many libraries
    /// got one.
    /// </summary>
    public async Task<int> GiveEveryLibraryAProfileAsync(UnitOfWork uow)
    {
        var given = 0;
        foreach (var library in (await ListAsync(uow).ConfigureAwait(false)).Where(l => l.RuleSetId is null))
        {
            var profile = await DefaultProfileIdAsync(uow, library.MediaType).ConfigureAwait(false);
            await uow.ExecuteAsync(
                "UPDATE libraries SET rule_set_id = @profile, updated_at = CURRENT_TIMESTAMP WHERE id = @id AND rule_set_id IS NULL",
                ("@profile", profile), ("@id", library.Id)).ConfigureAwait(false);
            given += 1;
        }

        if (given > 0)
        {
            Changed(uow);
        }

        return given;
    }

    public async Task<int> RuleSetUsageCountAsync(UnitOfWork uow, long ruleSetId) =>
        (int)await uow.CountAsync("SELECT COUNT(*) FROM libraries WHERE rule_set_id = @id OR library_rule_set_id = @id", ("@id", ruleSetId)).ConfigureAwait(false);

    public async Task<ProcessingRuleSetRecord> CreateRuleSetAsync(UnitOfWork uow, LibraryRules.RuleSetInput body)
    {
        var label = body.Name.Trim();
        if (await GetRuleSetByNameAsync(uow, label).ConfigureAwait(false) is not null)
        {
            throw new ProcessingLibraryException($"A rule set named '{label}' already exists.");
        }

        var row = LibraryRules.ApplyRuleSetFields(new ProcessingRuleSetRecord { Name = label }, body);
        await InsertRuleSetAsync(uow, row).ConfigureAwait(false);
        return await GetRuleSetByNameAsync(uow, label).ConfigureAwait(false) ?? throw new InvalidOperationException("Rule set insert race.");
    }

    public async Task<ProcessingRuleSetRecord> UpdateRuleSetAsync(UnitOfWork uow, ProcessingRuleSetRecord existing, LibraryRules.RuleSetInput body)
    {
        var label = body.Name.Trim();
        var clash = await GetRuleSetByNameAsync(uow, label).ConfigureAwait(false);
        if (clash is not null && clash.Id != existing.Id)
        {
            throw new ProcessingLibraryException($"A rule set named '{label}' already exists.");
        }

        var row = LibraryRules.ApplyRuleSetFields(existing with { Name = label }, body);
        await UpdateRuleSetRowAsync(uow, row).ConfigureAwait(false);
        return await GetRuleSetAsync(uow, existing.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("Rule set disappeared during update.");
    }

    public async Task DeleteRuleSetAsync(UnitOfWork uow, ProcessingRuleSetRecord row)
    {
        var used = await RuleSetUsageCountAsync(uow, row.Id).ConfigureAwait(false);
        if (used > 0)
        {
            throw new ProcessingLibraryException(
                $"{row.Name} is still used by {used} workflow{(used == 1 ? string.Empty : "s")}. " +
                "Point them at another rule set first — removing it would strip their audio and subtitle handling.");
        }

        await uow.ExecuteAsync("DELETE FROM rule_sets WHERE id = @id", ("@id", row.Id)).ConfigureAwait(false);
        Changed(uow);
    }

    private async Task InsertRuleSetAsync(UnitOfWork uow, ProcessingRuleSetRecord row)
    {
        await uow.ExecuteAsync(
            "INSERT INTO rule_sets (name, primary_audio_lang, secondary_audio_lang, tertiary_audio_lang, default_audio_slot, " +
            "remove_commentary, subtitle_mode, subtitle_langs_csv, preserve_forced_subs, preserve_default_subs, audio_preference_mode, " +
            "audio_sorters_json, subtitle_sorters_json, keep_original_language, original_language_additional_csv, " +
            "original_language_keep_only_first, original_language_first_if_none, original_language_treat_empty_as_original, " +
            "remove_images, remove_attachments, remove_title, remove_language_tags, remove_other_metadata, " +
            "remove_hearing_impaired_subs, audio_keep_mode, subtitle_max_per_language, subtitle_quality_strategy, " +
            "standardize_track_names, track_name_template, track_name_override_forced, track_name_override_hearing_impaired, " +
            "track_name_override_commentary, track_name_override_audio_description, clear_video_track_names, remove_chapters) VALUES " +
            "(@name, @primary_audio_lang, @secondary_audio_lang, @tertiary_audio_lang, @default_audio_slot, @remove_commentary, " +
            "@subtitle_mode, @subtitle_langs_csv, @preserve_forced_subs, @preserve_default_subs, @audio_preference_mode, " +
            "@audio_sorters_json, @subtitle_sorters_json, @keep_original_language, @original_language_additional_csv, " +
            "@original_language_keep_only_first, @original_language_first_if_none, @original_language_treat_empty_as_original, " +
            "@remove_images, @remove_attachments, @remove_title, @remove_language_tags, @remove_other_metadata, " +
            "@remove_hearing_impaired_subs, @audio_keep_mode, @subtitle_max_per_language, @subtitle_quality_strategy, " +
            "@standardize_track_names, @track_name_template, @track_name_override_forced, @track_name_override_hearing_impaired, " +
            "@track_name_override_commentary, @track_name_override_audio_description, @clear_video_track_names, @remove_chapters)",
            RuleSetParameters(row)).ConfigureAwait(false);
        Changed(uow);
    }

    private async Task UpdateRuleSetRowAsync(UnitOfWork uow, ProcessingRuleSetRecord row)
    {
        await uow.ExecuteAsync(
            "UPDATE rule_sets SET name=@name, primary_audio_lang=@primary_audio_lang, secondary_audio_lang=@secondary_audio_lang, " +
            "tertiary_audio_lang=@tertiary_audio_lang, default_audio_slot=@default_audio_slot, remove_commentary=@remove_commentary, " +
            "subtitle_mode=@subtitle_mode, subtitle_langs_csv=@subtitle_langs_csv, preserve_forced_subs=@preserve_forced_subs, " +
            "preserve_default_subs=@preserve_default_subs, audio_preference_mode=@audio_preference_mode, audio_sorters_json=@audio_sorters_json, " +
            "subtitle_sorters_json=@subtitle_sorters_json, keep_original_language=@keep_original_language, " +
            "original_language_additional_csv=@original_language_additional_csv, original_language_keep_only_first=@original_language_keep_only_first, " +
            "original_language_first_if_none=@original_language_first_if_none, " +
            "original_language_treat_empty_as_original=@original_language_treat_empty_as_original, remove_images=@remove_images, " +
            "remove_attachments=@remove_attachments, remove_title=@remove_title, remove_language_tags=@remove_language_tags, " +
            "remove_other_metadata=@remove_other_metadata, remove_hearing_impaired_subs=@remove_hearing_impaired_subs, " +
            "audio_keep_mode=@audio_keep_mode, subtitle_max_per_language=@subtitle_max_per_language, " +
            "subtitle_quality_strategy=@subtitle_quality_strategy, standardize_track_names=@standardize_track_names, " +
            "track_name_template=@track_name_template, track_name_override_forced=@track_name_override_forced, " +
            "track_name_override_hearing_impaired=@track_name_override_hearing_impaired, " +
            "track_name_override_commentary=@track_name_override_commentary, " +
            "track_name_override_audio_description=@track_name_override_audio_description, " +
            "clear_video_track_names=@clear_video_track_names, remove_chapters=@remove_chapters, " +
            "updated_at=CURRENT_TIMESTAMP WHERE id=@id",
            [.. RuleSetParameters(row), ("@id", row.Id)]).ConfigureAwait(false);
        Changed(uow);
    }

    private static (string, object?)[] RuleSetParameters(ProcessingRuleSetRecord row) =>
    [
        ("@name", row.Name),
        ("@primary_audio_lang", row.PrimaryAudioLang),
        ("@secondary_audio_lang", row.SecondaryAudioLang),
        ("@tertiary_audio_lang", row.TertiaryAudioLang),
        ("@default_audio_slot", row.DefaultAudioSlot),
        ("@remove_commentary", row.RemoveCommentary ? 1 : 0),
        ("@subtitle_mode", row.SubtitleMode),
        ("@subtitle_langs_csv", row.SubtitleLangsCsv),
        ("@preserve_forced_subs", row.PreserveForcedSubs ? 1 : 0),
        ("@preserve_default_subs", row.PreserveDefaultSubs ? 1 : 0),
        ("@audio_preference_mode", row.AudioPreferenceMode),
        ("@audio_sorters_json", row.AudioSortersJson),
        ("@subtitle_sorters_json", row.SubtitleSortersJson),
        ("@keep_original_language", row.KeepOriginalLanguage ? 1 : 0),
        ("@original_language_additional_csv", row.OriginalLanguageAdditionalCsv),
        ("@original_language_keep_only_first", row.OriginalLanguageKeepOnlyFirst ? 1 : 0),
        ("@original_language_first_if_none", row.OriginalLanguageFirstIfNone ? 1 : 0),
        ("@original_language_treat_empty_as_original", row.OriginalLanguageTreatEmptyAsOriginal ? 1 : 0),
        ("@remove_images", row.RemoveImages ? 1 : 0),
        ("@remove_attachments", row.RemoveAttachments ? 1 : 0),
        ("@remove_title", row.RemoveTitle ? 1 : 0),
        ("@remove_language_tags", row.RemoveLanguageTags ? 1 : 0),
        ("@remove_other_metadata", row.RemoveOtherMetadata ? 1 : 0),
        ("@remove_hearing_impaired_subs", row.RemoveHearingImpairedSubs ? 1 : 0),
        ("@audio_keep_mode", row.AudioKeepMode),
        ("@subtitle_max_per_language", row.SubtitleMaxPerLanguage),
        ("@subtitle_quality_strategy", row.SubtitleQualityStrategy),
        ("@standardize_track_names", row.StandardizeTrackNames ? 1 : 0),
        ("@track_name_template", row.TrackNameTemplate),
        ("@track_name_override_forced", row.TrackNameOverrides.Forced),
        ("@track_name_override_hearing_impaired", row.TrackNameOverrides.HearingImpaired),
        ("@track_name_override_commentary", row.TrackNameOverrides.Commentary),
        ("@track_name_override_audio_description", row.TrackNameOverrides.AudioDescription),
        ("@clear_video_track_names", row.ClearVideoTrackNames ? 1 : 0),
        ("@remove_chapters", row.RemoveChapters ? 1 : 0),
    ];

    private static ProcessingRuleSetRecord ReadRuleSet(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = SqliteValues.GetString(reader, 1),
        PrimaryAudioLang = SqliteValues.GetString(reader, 2),
        SecondaryAudioLang = SqliteValues.GetString(reader, 3),
        TertiaryAudioLang = SqliteValues.GetString(reader, 4),
        DefaultAudioSlot = SqliteValues.GetString(reader, 5),
        RemoveCommentary = SqliteValues.GetBool(reader, 6),
        SubtitleMode = SqliteValues.GetString(reader, 7),
        SubtitleLangsCsv = SqliteValues.GetString(reader, 8),
        PreserveForcedSubs = SqliteValues.GetBool(reader, 9),
        PreserveDefaultSubs = SqliteValues.GetBool(reader, 10),
        AudioPreferenceMode = SqliteValues.GetString(reader, 11),
        AudioSortersJson = SqliteValues.GetString(reader, 12),
        SubtitleSortersJson = SqliteValues.GetString(reader, 13),
        KeepOriginalLanguage = SqliteValues.GetBool(reader, 14),
        OriginalLanguageAdditionalCsv = SqliteValues.GetString(reader, 15),
        OriginalLanguageKeepOnlyFirst = SqliteValues.GetBool(reader, 16),
        OriginalLanguageFirstIfNone = SqliteValues.GetBool(reader, 17),
        OriginalLanguageTreatEmptyAsOriginal = SqliteValues.GetBool(reader, 18),
        RemoveImages = SqliteValues.GetBool(reader, 19),
        RemoveAttachments = SqliteValues.GetBool(reader, 20),
        RemoveTitle = SqliteValues.GetBool(reader, 21),
        RemoveLanguageTags = SqliteValues.GetBool(reader, 22),
        RemoveOtherMetadata = SqliteValues.GetBool(reader, 23),
        RemoveHearingImpairedSubs = SqliteValues.GetBool(reader, 24),
        AudioKeepMode = SqliteValues.GetString(reader, 25),
        SubtitleMaxPerLanguage = (int)SqliteValues.GetInt64(reader, 26),
        SubtitleQualityStrategy = SqliteValues.GetString(reader, 27),
        StandardizeTrackNames = SqliteValues.GetBool(reader, 28),
        TrackNameTemplate = SqliteValues.GetString(reader, 29),
        TrackNameOverrides = new TrackNameOverrides
        {
            Forced = SqliteValues.GetString(reader, 30),
            HearingImpaired = SqliteValues.GetString(reader, 31),
            Commentary = SqliteValues.GetString(reader, 32),
            AudioDescription = SqliteValues.GetString(reader, 33),
        },
        ClearVideoTrackNames = SqliteValues.GetBool(reader, 34),
        RemoveChapters = SqliteValues.GetBool(reader, 35),
        CreatedAt = SqliteValues.GetDateTime(reader, 36),
        UpdatedAt = SqliteValues.GetDateTime(reader, 37),
    };
}
