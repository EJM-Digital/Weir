using FsCheck;
using FsCheck.Fluent;
using Weir.Core.MediaManagers;
using Weir.TestSupport;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// Which videos a hand-off of a release folder means (<see cref="IntakeRules.ChooseFolderVideos"/>): the main videos, never a sample
/// unless the folder holds nothing else, in an order that does not depend on the order the folder was read in.
/// </summary>
public sealed class ReleaseFolderExpansionPropertyTests
{
    private static readonly Gen<string> Word = Gen.Elements("Film", "film", "Sample", "sample", "SAMPLE", "Samples", "resample", "Extras", "Disc 1", "Part", "x264", "2001");

    /// <summary>A file or folder name: words joined by the characters release names use, so a word is sometimes the whole name and sometimes inside it.</summary>
    private static readonly Gen<string> NameWithWords =
        from count in Gen.Choose(1, 3)
        from words in Gen.ArrayOf(Word, count)
        from joiners in Gen.ArrayOf(Gen.Elements(".", "-", "_", " ", "(", ")", "ab"), count)
        select string.Concat(words.Zip(joiners, (word, joiner) => word + joiner)).TrimEnd('.', ' ');

    private static readonly Gen<IReadOnlyList<string>> VideoParts =
        from depth in Gen.Choose(1, 3)
        from parts in Gen.ArrayOf(NameWithWords, depth)
        select (IReadOnlyList<string>)parts;

    private static readonly Gen<List<IReadOnlyList<string>>> Videos = Gen.ListOf(VideoParts).Select(list => list.ToList());

    [Fact]
    public void Samples_are_left_out_unless_every_video_is_one_and_what_is_left_is_never_empty_when_there_were_videos()
    {
        PropertyChecks.ForAll(Videos.Zip(Gen.Elements(true, false)), found =>
        {
            var (videos, windows) = found;
            var chosen = IntakeRules.ChooseFolderVideos(videos, windows);
            Assert.Equal(videos.Count == 0, chosen.Count == 0);

            var hasMain = videos.Any(parts => !IsSample(parts));
            Assert.Equal(
                hasMain ? videos.Count(parts => !IsSample(parts)) : videos.Count,
                chosen.Count);
            if (hasMain)
            {
                Assert.All(chosen, parts => Assert.False(IsSample(parts), string.Join('/', parts)));
            }
        });
    }

    [Fact]
    public void The_videos_chosen_are_in_order_and_the_same_whatever_order_the_folder_listed_them_in()
    {
        var shuffled =
            from videos in Videos
            from windows in Gen.Elements(true, false)
            from again in Gen.Shuffle(videos.ToArray())
            select (videos, again.ToList(), windows);

        PropertyChecks.ForAll(shuffled, found =>
        {
            var (videos, reordered, windows) = found;
            var first = IntakeRules.ChooseFolderVideos(videos, windows);
            var second = IntakeRules.ChooseFolderVideos(reordered, windows);
            Assert.Equal(Keys(first, windows), Keys(second, windows));
            for (var i = 1; i < first.Count; i++)
            {
                Assert.True(IntakeRules.ComparePathParts(first[i - 1], first[i], windows) <= 0);
            }
        });
    }

    [Fact]
    public void Choosing_from_what_was_already_chosen_changes_nothing()
    {
        PropertyChecks.ForAll(Videos.Zip(Gen.Elements(true, false)), found =>
        {
            var (videos, windows) = found;
            var once = IntakeRules.ChooseFolderVideos(videos, windows);
            Assert.Equal(Keys(once, windows), Keys(IntakeRules.ChooseFolderVideos(once, windows), windows));
        });
    }

    [Fact]
    public void A_name_is_a_sample_exactly_when_the_word_sample_stands_alone_in_it()
    {
        PropertyChecks.ForAll(NameWithWords, name =>
        {
            var tokens = name.Split(
                [.. Enumerable.Range(0, 128).Select(code => (char)code).Where(character => !char.IsAsciiLetterOrDigit(character))]);
            Assert.Equal(
                tokens.Any(token => token.Equals("sample", StringComparison.OrdinalIgnoreCase)),
                IntakeRules.SamplePart().IsMatch(name));
        });
    }

    /// <summary>Two orders are the same where the platform does not tell names apart by case, and nowhere else.</summary>
    private static string[] Keys(IEnumerable<IReadOnlyList<string>> videos, bool windows) =>
        [.. videos.Select(parts => string.Join('/', parts.Select(part => windows ? part.ToLowerInvariant() : part)))];

    private static bool IsSample(IReadOnlyList<string> parts) => parts.Any(part => IntakeRules.SamplePart().IsMatch(part));
}
