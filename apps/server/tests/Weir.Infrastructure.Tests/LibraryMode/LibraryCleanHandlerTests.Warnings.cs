using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>What a clean says when the swap that carried it out had something to mention, and the cleans that change only the order of tracks.</summary>
public sealed partial class LibraryCleanHandlerTests
{
    /// <summary>The real swap file system, except that it cannot say how much room the volume has.</summary>
    private sealed class UnreadableSpace(ISwapFileSystem inner) : ISwapFileSystem
    {
        public bool FileExists(string path) => inner.FileExists(path);

        public SourceFingerprint Fingerprint(string path) => inner.Fingerprint(path);

        public int? LinkCount(string path) => inner.LinkCount(path);

        public bool IsReadOnly(string path) => inner.IsReadOnly(path);

        public long? AvailableFreeBytes(string directory) => null;

        public void ProbeWrite(string directory) => inner.ProbeWrite(directory);

        public bool IsInUse(string path) => inner.IsInUse(path);

        public void CopyPermissions(string source, string destination) => inner.CopyPermissions(source, destination);

        public void Move(string source, string destination) => inner.Move(source, destination);

        public void Delete(string path) => inner.Delete(path);

        public IEnumerable<string> EnumerateLeftovers(string folder) => inner.EnumerateLeftovers(folder);

        public void EnsureDirectory(string directory) => inner.EnsureDirectory(directory);

        public string ContentHash(string path) => inner.ContentHash(path);

        public bool TryReserve(string path) => inner.TryReserve(path);

        public void ReplaceReservation(string source, string destination) => inner.ReplaceReservation(source, destination);

        public void CopyOverReservation(string source, string destination) => inner.CopyOverReservation(source, destination);
    }

    [Fact]
    public async Task A_clean_carried_out_with_something_to_mention_says_so_in_what_it_records()
    {
        var library = await LibraryAsync();
        var path = _libraryFolder.Join("film.mkv");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;

        await RunCleanAsync(await EnqueueCleanAsync(library, path, confirmFinalRemoval: true), Handler(swapFiles: new UnreadableSpace(PhysicalSwapFileSystem.Instance)));

        Assert.Contains("Weir could not read the free space on this file's volume.", await CleanedDetailAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_choice_that_only_puts_the_tracks_in_another_order_is_carried_out_without_removing_any()
    {
        // What the written file reads as: the Japanese track first, the English second.
        _media.DefaultProbe = """{"format":{"duration":"100.0"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264","duration":"100.0"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"},"disposition":{"default":1},"duration":"100.0"},{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"},"duration":"100.0"}]}""";

        var (_, type, title) = await RunChosenAsync(Keeping(0, 2, 1), FakeMediaRunner.EnglishAndJapanese);

        Assert.True(type == Weir.Core.Activity.LibraryActivityEventTypes.FileCleaned, title);
        Assert.Single(_media.Remuxes);
    }
}
