using System.Reflection;
using System.Text.Json;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A rule set decides which tracks a file keeps and what is stripped from it, and the original may then be deleted. Every setting a
/// person can save must reach the configuration a real pass runs with (<see cref="RemuxPassPaths.RulesConfigFor"/>): a setting that is
/// saved and then ignored changes nothing, and nobody is told.
/// </summary>
public sealed class RuleSetEveryFieldMappingTests
{
    private static readonly string[] NotRules = [nameof(ProcessingRuleSetRecord.Id), nameof(ProcessingRuleSetRecord.Name), nameof(ProcessingRuleSetRecord.CreatedAt), nameof(ProcessingRuleSetRecord.UpdatedAt)];

    private const string AudioSorters = "[{\"field\": \"channels\", \"value\": \">=5.1\"}, {\"field\": \"language\", \"value\": \"eng\"}]";
    private const string SubtitleSorters = "[{\"field\": \"language\", \"value\": \"fre\"}, {\"field\": \"forced\"}]";

    private static ProcessingRuleSetRecord Plain() => new() { Name = "Plain" };

    /// <summary>A value different from the default for a setting, in a form the pass keeps as it is.</summary>
    private static object Changed(PropertyInfo property) => property.Name switch
    {
        nameof(ProcessingRuleSetRecord.PrimaryAudioLang) => "jpn",
        nameof(ProcessingRuleSetRecord.SecondaryAudioLang) => "fre",
        nameof(ProcessingRuleSetRecord.TertiaryAudioLang) => "ger",
        nameof(ProcessingRuleSetRecord.DefaultAudioSlot) => "tertiary",
        nameof(ProcessingRuleSetRecord.SubtitleMode) => "keep_listed",
        nameof(ProcessingRuleSetRecord.SubtitleLangsCsv) => "eng,fre",
        nameof(ProcessingRuleSetRecord.AudioPreferenceMode) => "quality_all_languages",
        nameof(ProcessingRuleSetRecord.AudioSortersJson) => AudioSorters,
        nameof(ProcessingRuleSetRecord.SubtitleSortersJson) => SubtitleSorters,
        nameof(ProcessingRuleSetRecord.OriginalLanguageAdditionalCsv) => "jpn,kor",
        nameof(ProcessingRuleSetRecord.AudioKeepMode) => RemuxRuleValues.AudioKeepModePerLanguage,
        nameof(ProcessingRuleSetRecord.SubtitleQualityStrategy) => RemuxRuleValues.SubtitleStrategyImageFirst,
        nameof(ProcessingRuleSetRecord.TrackNameTemplate) => "{language} {codec}",
        nameof(ProcessingRuleSetRecord.TrackNameOverrides) => new TrackNameOverrides { Forced = "{language} (Forced)", HearingImpaired = "{language} (SDH)", Commentary = "{language} (Commentary)", AudioDescription = "{language} (AD)" },
        nameof(ProcessingRuleSetRecord.SubtitleMaxPerLanguage) => 3,
        _ when property.PropertyType == typeof(bool) => !(bool)property.GetValue(Plain())!,
        _ => throw new InvalidOperationException(
            $"The rule set has a new setting, {property.Name}. Give this test a changed value for it, and make sure it reaches RulesConfigFor."),
    };

    public static TheoryData<string> Settings()
    {
        var data = new TheoryData<string>();
        foreach (var property in typeof(ProcessingRuleSetRecord).GetProperties().Where(property => !NotRules.Contains(property.Name)))
        {
            data.Add(property.Name);
        }

        return data;
    }

    private static string Describe(ProcessingRuleSetRecord ruleSet) => JsonSerializer.Serialize(RemuxPassPaths.RulesConfigFor(ruleSet));

    [Theory]
    [MemberData(nameof(Settings))]
    public void Every_saved_setting_changes_the_rules_a_real_pass_runs_with(string setting)
    {
        var property = typeof(ProcessingRuleSetRecord).GetProperty(setting)!;
        var changed = Plain();
        property.SetValue(changed, Changed(property));

        Assert.NotEqual(Describe(Plain()), Describe(changed));
    }

    [Fact]
    public void The_languages_and_the_default_slot_reach_the_pass()
    {
        var config = RemuxPassPaths.RulesConfigFor(Plain() with
        {
            PrimaryAudioLang = "jpn",
            SecondaryAudioLang = "fre",
            TertiaryAudioLang = "ger",
            DefaultAudioSlot = "secondary",
        });

        Assert.Equal(("jpn", "fre", "ger", "secondary"), (config.PrimaryAudioLang, config.SecondaryAudioLang, config.TertiaryAudioLang, config.DefaultAudioSlot));
    }

    [Fact]
    public void The_audio_order_and_the_way_audio_is_preferred_reach_the_pass()
    {
        var config = RemuxPassPaths.RulesConfigFor(Plain() with { AudioPreferenceMode = "preferred_langs_strict", AudioSortersJson = AudioSorters });

        Assert.Equal("preferred_langs_strict", config.AudioPreferenceMode);
        Assert.Equal(AudioSorters, config.AudioSortersJson);
    }

    [Fact]
    public void The_subtitle_order_reaches_the_pass() =>
        Assert.Equal(SubtitleSorters, RemuxPassPaths.RulesConfigFor(Plain() with { SubtitleSortersJson = SubtitleSorters }).SubtitleSortersJson);

    [Fact]
    public void The_commentary_choice_reaches_the_pass()
    {
        Assert.False(RemuxPassPaths.RulesConfigFor(Plain()).RemoveCommentary);
        Assert.True(RemuxPassPaths.RulesConfigFor(Plain() with { RemoveCommentary = true }).RemoveCommentary);
    }

    [Fact]
    public void The_subtitle_languages_are_split_on_commas_and_trimmed()
    {
        var config = RemuxPassPaths.RulesConfigFor(Plain() with { SubtitleMode = "keep_listed", SubtitleLangsCsv = " eng , fre,, ger ,  " });

        Assert.Equal(["eng", "fre", "ger"], config.SubtitleLangs);
        Assert.Equal(RemuxRuleValues.SubtitleModeKeepSelected, config.SubtitleMode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_two_subtitle_flags_to_keep_reach_the_pass_on_their_own(bool forced, bool isDefault)
    {
        var config = RemuxPassPaths.RulesConfigFor(Plain() with { PreserveForcedSubs = forced, PreserveDefaultSubs = isDefault });

        Assert.Equal((forced, isDefault), (config.PreserveForcedSubs, config.PreserveDefaultSubs));
    }

    [Fact]
    public void The_four_original_language_options_reach_the_pass_each_on_its_own()
    {
        var on = RemuxPassPaths.RulesConfigFor(Plain() with { KeepOriginalLanguage = true }).OriginalLanguage!;
        var extra = RemuxPassPaths.RulesConfigFor(Plain() with { OriginalLanguageAdditionalCsv = "JPN, ko ,, jpn" }).OriginalLanguage!;
        var all = RemuxPassPaths.RulesConfigFor(Plain() with { OriginalLanguageKeepOnlyFirst = false }).OriginalLanguage!;
        var none = RemuxPassPaths.RulesConfigFor(Plain() with { OriginalLanguageFirstIfNone = false }).OriginalLanguage!;
        var untagged = RemuxPassPaths.RulesConfigFor(Plain() with { OriginalLanguageTreatEmptyAsOriginal = true }).OriginalLanguage!;

        Assert.True(on.Enabled);
        Assert.Equal(["jpn", "kor"], extra.AdditionalLanguages);
        Assert.Equal((true, true, false), (on.KeepOnlyFirst, on.FirstIfNone, on.TreatEmptyAsOriginal));
        Assert.False(all.KeepOnlyFirst);
        Assert.False(none.FirstIfNone);
        Assert.True(untagged.TreatEmptyAsOriginal);
        Assert.False(extra.Enabled || all.Enabled || none.Enabled || untagged.Enabled);
    }

    [Theory]
    [InlineData(nameof(ProcessingRuleSetRecord.RemoveImages))]
    [InlineData(nameof(ProcessingRuleSetRecord.RemoveAttachments))]
    [InlineData(nameof(ProcessingRuleSetRecord.RemoveTitle))]
    [InlineData(nameof(ProcessingRuleSetRecord.RemoveLanguageTags))]
    [InlineData(nameof(ProcessingRuleSetRecord.RemoveOtherMetadata))]
    public void Each_metadata_removal_reaches_the_pass_alone(string removal)
    {
        var ruleSet = Plain();
        typeof(ProcessingRuleSetRecord).GetProperty(removal)!.SetValue(ruleSet, true);

        var metadata = RemuxPassPaths.RulesConfigFor(ruleSet).Metadata;

        var turnedOn = new[]
        {
            (nameof(ProcessingRuleSetRecord.RemoveImages), metadata.RemoveImages),
            (nameof(ProcessingRuleSetRecord.RemoveAttachments), metadata.RemoveAttachments),
            (nameof(ProcessingRuleSetRecord.RemoveTitle), metadata.RemoveTitle),
            (nameof(ProcessingRuleSetRecord.RemoveLanguageTags), metadata.RemoveLanguageTags),
            (nameof(ProcessingRuleSetRecord.RemoveOtherMetadata), metadata.RemoveOtherMetadata),
        }.Where(flag => flag.Item2).Select(flag => flag.Item1);
        Assert.Equal([removal], turnedOn);
    }
}
