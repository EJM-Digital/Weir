namespace Weir.Core.Rules;

/// <summary>The stored values the planner compares against, kept as strings so unknown stored values behave predictably.</summary>
public static class RemuxRuleValues
{
    public const string SubtitleModeRemoveAll = "remove_all";
    public const string SubtitleModeKeepSelected = "keep_selected";

    /// <summary>
    /// Keep every subtitle track, whatever its language: the stored default and what Setup › Rules › Profiles calls
    /// "Keep all subtitles". It must not be read as keep-selected with an empty language list, which would
    /// remove every subtitle, forced and default included.
    /// </summary>
    public const string SubtitleModeKeepAll = "keep_all";
    public const string DefaultAudioSlotPrimary = "primary";
    public const string DefaultAudioSlotSecondary = "secondary";
    public const string DefaultAudioSlotTertiary = "tertiary";
    public const string PolicyPreferredLangsQuality = "preferred_langs_quality";
    public const string PolicyPreferredLangsStrict = "preferred_langs_strict";
    public const string PolicyQualityAllLanguages = "quality_all_languages";

    /// <summary>Issue #497: keep exactly one audio track overall (the default).</summary>
    public const string AudioKeepModeSingle = "single";

    /// <summary>Issue #497: keep the best track of each configured language slot that has one.</summary>
    public const string AudioKeepModePerLanguage = "per_language";

    /// <summary>Issue #497: text (SRT/ASS/WebVTT/mov_text) over image (PGS/VobSub/DVB), the default.</summary>
    public const string SubtitleStrategyTextFirst = "text_first";

    public const string SubtitleStrategyImageFirst = "image_first";

    /// <summary>Issue #497: a hearing-impaired (SDH) track ranks first.</summary>
    public const string SubtitleStrategyAccessibility = "accessibility";
}
