using FsCheck;
using FsCheck.Fluent;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.TestSupport;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// <see cref="OutputCollision"/> on real folders: for every policy, every spelling of it, and every mix of an output that is or is not
/// already there, bigger or smaller, older or newer, the output goes where the policy says and nothing is changed on disk by deciding.
/// </summary>
public sealed class OutputCollisionPropertyTests
{
    private const int FileRuns = 100;

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<string> OutputName =
        from stem in PathGenerators.Name
        from shape in Gen.Elements(Shape.Plain, Shape.NoExtension, Shape.DotFile, Shape.ManyDots)
        from inner in PathGenerators.Name
        select shape switch
        {
            Shape.NoExtension => stem,
            Shape.DotFile => "." + stem,
            Shape.ManyDots => stem + "." + inner + ".mkv",
            _ => stem + ".mkv",
        };

    private sealed record Existing(bool Present, bool IsDirectory, int Bytes, int ModifiedSeconds);

    private sealed record Scenario(string Name, string Policy, Existing Existing, int? StagedBytes, int? SourceModifiedSeconds);

    private static readonly Gen<Scenario> Scenarios =
        from name in OutputName
        from policy in PolicyText()
        from present in Gen.Elements(true, true, false)
        from directory in Gen.Elements(false, false, false, true)
        from bytes in Gen.Choose(0, 40)
        from modified in Gen.Choose(0, 4)
        from staged in Gen.OneOf(
            Gen.Constant<int?>(null),
            Gen.Choose(0, 40).Select(size => (int?)size),
            Gen.Elements(-1, 0, 0, 1).Select(step => (int?)Math.Max(0, bytes + step)))
        from source in Gen.OneOf(
            Gen.Constant<int?>(null),
            Gen.Choose(0, 4).Select(seconds => (int?)seconds),
            Gen.Elements(-1, 0, 0, 1).Select(step => (int?)Math.Max(0, modified + step)))
        select new Scenario(name, policy, new Existing(present, directory, bytes, modified), staged, source);

    [Fact]
    public void A_policy_is_any_spelling_of_a_known_one_and_anything_else_is_replace()
    {
        var text = Gen.OneOf(PolicyText(), Gen.Elements("keep", "KEEP_BOTH ", "keep both", "skip!", string.Empty, "   ", "replace_if_larger_", "é"));
        PropertyChecks.ForAll(text, raw =>
        {
            var policy = OutputCollision.NormalizePolicy(raw);
            var known = raw.Trim().ToLowerInvariant();
            Assert.Equal(OutputCollision.Policies.Contains(known) ? known : OutputCollision.Replace, policy);
            Assert.Equal(policy, OutputCollision.NormalizePolicy(policy));
        });
        Assert.Equal(OutputCollision.Replace, OutputCollision.NormalizePolicy(null));
    }

    [Fact]
    public void The_next_free_name_is_the_lowest_number_nobody_has_taken_and_there_is_none_when_all_are_taken()
    {
        const int limit = 6;
        var taken =
            from name in OutputName
            from numbers in Gen.SubListOf(Enumerable.Range(2, limit - 1).ToArray())
            select (name, numbers: numbers.ToList());

        PropertyChecks.ForAll(taken, found =>
        {
            using var folder = new TempDirectory();
            var final = folder.Join(found.name);
            File.WriteAllText(final, "film");
            var (stem, suffix) = Split(found.name);
            foreach (var number in found.numbers)
            {
                File.WriteAllText(folder.Join($"{stem} ({number}){suffix}"), "other");
            }

            var next = OutputCollision.NextFreePath(final, limit);

            var lowest = Enumerable.Range(2, limit - 1).Cast<int?>().FirstOrDefault(number => !found.numbers.Contains(number!.Value));
            if (lowest is null)
            {
                Assert.Null(next);
                return;
            }

            Assert.Equal(folder.Path, Path.GetDirectoryName(next));
            Assert.Equal($"{stem} ({lowest}){suffix}", Path.GetFileName(next));
            Assert.False(File.Exists(next), $"{Path.GetFileName(next)} is already there.");
        }, FileRuns);
    }

    [Fact]
    public void Deciding_follows_the_policy_and_changes_nothing_on_disk()
    {
        PropertyChecks.ForAll(Scenarios, scenario =>
        {
            using var folder = new TempDirectory();
            var final = folder.Join(scenario.Name);
            var existing = scenario.Existing;
            if (existing.Present)
            {
                if (existing.IsDirectory)
                {
                    Directory.CreateDirectory(final);
                }
                else
                {
                    Write(final, existing.Bytes, existing.ModifiedSeconds);
                }
            }

            string? staged = null;
            if (scenario.StagedBytes is { } stagedBytes)
            {
                staged = folder.Join("staged.tmp");
                Write(staged, stagedBytes, 0);
            }

            string? source = null;
            if (scenario.SourceModifiedSeconds is { } sourceSeconds)
            {
                source = folder.Join("source.mkv");
                Write(source, 1, sourceSeconds);
            }

            var before = Listing(folder.Path);
            var decision = OutputCollision.Decide(final, source, staged, scenario.Policy);

            Assert.Equal(before, Listing(folder.Path));
            Assert.Equal(OutputCollision.NormalizePolicy(scenario.Policy), decision.Policy);
            Assert.Equal(decision.Action == "write", decision.Wrote);
            Assert.Contains(decision.Action, new[] { "write", "skip" });
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
            Assert.DoesNotContain(folder.Path, decision.Reason, StringComparison.Ordinal);

            Assert.Equal(DecisionFor(scenario), (decision.Action, decision.Destination == final, decision.ReplacedExisting));
            if (decision.Wrote && decision.Destination != final)
            {
                Assert.False(File.Exists(decision.Destination) || Directory.Exists(decision.Destination));
                Assert.Equal(folder.Path, Path.GetDirectoryName(decision.Destination));
            }
        }, FileRuns);
    }

    /// <summary>What the policy asks for, worked out from the scenario alone: the action, whether it is at the planned path, and whether it replaces what is there.</summary>
    private static (string Action, bool AtPlannedPath, bool Replaces) DecisionFor(Scenario scenario)
    {
        var policy = OutputCollision.NormalizePolicy(scenario.Policy);
        var existing = scenario.Existing;
        if (!existing.Present)
        {
            return ("write", true, false);
        }

        return policy switch
        {
            OutputCollision.Skip => ("skip", true, false),
            OutputCollision.KeepBoth => ("write", false, false),
            OutputCollision.ReplaceIfLarger => scenario.StagedBytes is { } staged && staged > (existing.IsDirectory ? 0 : existing.Bytes)
                ? ("write", true, true)
                : ("skip", true, false),
            OutputCollision.ReplaceIfNewer => !existing.IsDirectory && scenario.SourceModifiedSeconds is { } source && source > existing.ModifiedSeconds
                ? ("write", true, true)
                : ("skip", true, false),
            _ => ("write", true, true),
        };
    }

    private static Gen<string> PolicyText() =>
        from policy in Gen.Elements(OutputCollision.Policies.ToArray())
        from style in Gen.Elements(PathGenerators.CaseStyle.Keep, PathGenerators.CaseStyle.Upper, PathGenerators.CaseStyle.Lower)
        from before in Gen.Elements(string.Empty, " ", "\t")
        from after in Gen.Elements(string.Empty, " ", "\r\n")
        select before + PathGenerators.Written(style, policy) + after;

    private static void Write(string path, int bytes, int modifiedSeconds)
    {
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Epoch.AddSeconds(modifiedSeconds));
    }

    private static string[] Listing(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(folder, path)}|{(File.Exists(path) ? new FileInfo(path).Length : -1)}|{File.GetLastWriteTimeUtc(path):O}")
            .Order(StringComparer.Ordinal)];

    private static (string Stem, string Suffix) Split(string name)
    {
        var index = name.LastIndexOf('.');
        return index <= 0 || index == name.Length - 1 ? (name, string.Empty) : (name[..index], name[index..]);
    }

    private enum Shape
    {
        Plain,
        NoExtension,
        DotFile,
        ManyDots,
    }
}
