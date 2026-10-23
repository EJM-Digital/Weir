using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>How a pass reads the file it is waiting on, and what it does with a rejected file it cannot delete.</summary>
public sealed partial class RemuxPassHandlerTests
{
    [Fact]
    public void A_source_file_is_told_apart_from_its_size_and_modification_time()
    {
        var file = _folders.Source("Movie/file.mkv");
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 10, 20, 1, 2, 3, DateTimeKind.Utc));

        var fingerprint = RemuxPassHandler.SourceFingerprint(new WireString(file));

        Assert.Equal(
            $"{new FileInfo(file).Length.ToString(CultureInfo.InvariantCulture)}:{new DateTime(2026, 10, 20, 1, 2, 3, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)}",
            fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing.mkv")]
    [InlineData("bad\0name.mkv")]
    public void A_source_file_that_cannot_be_measured_has_no_fingerprint(string? path)
    {
        var inspected = path is null ? null : new WireString(path.Length == 0 || path.Contains('\0', StringComparison.Ordinal) ? path : Path.Join(_folders.Root, path));

        Assert.Null(RemuxPassHandler.SourceFingerprint(inspected));
    }

    [Fact]
    public async Task A_look_at_a_file_that_could_not_be_measured_counts_as_the_look_before_and_books_the_next_one_without_a_fingerprint()
    {
        var result = new WireObject().Set("not_ready_kind", RemuxPassRunner.UnreadableWait);

        await Handler().SettleUnreadableSourceAsync(7, new WireObject(), null, result, CancellationToken.None);

        var next = Assert.Single(await _fixture.Jobs.ListAsync());
        var payload = (WireObject)WireJsonParser.Parse(next.PayloadJson!);
        Assert.EndsWith(":unreadable-wait:7:1", next.DedupeKey, StringComparison.Ordinal);
        Assert.Equal(1, ((WireInteger)payload["unreadable_looks"]).Value);
        Assert.False(payload.ContainsKey("unreadable_fingerprint"));
        Assert.True(((WireBool)result["retry_scheduled"]).Value);
    }

    [WindowsFact("A file held open without delete sharing cannot be deleted only on Windows")]
    public async Task A_rejected_file_that_is_locked_is_kept_and_the_rejection_says_why()
    {
        var library = await LibraryAsync(rejectedFileAction: "delete_file");
        await EnglishOnlyRulesAsync(library);
        var source = _folders.Source("Foreign Again (2020).mkv");
        _media.DefaultProbe = FakeMediaRunner.JapaneseOnly;
        await FileRowAsync(library, "Foreign Again (2020).mkv");
        using var held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Handler().HandleAsync(
            Context(31, $$$"""{"relative_media_path":"Foreign Again (2020).mkv","media_scope":"movie","library_id":{{{library}}}}"""), CancellationToken.None);

        Assert.True(File.Exists(source));
        Assert.Equal(ProcessingFileStatuses.Rejected, await ScalarText("SELECT status FROM files"));
        Assert.Contains("locked or unavailable", await ScalarText("SELECT status_reason FROM files"), StringComparison.Ordinal);
    }
}
