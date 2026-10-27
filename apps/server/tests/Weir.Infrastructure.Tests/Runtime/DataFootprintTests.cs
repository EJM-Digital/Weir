using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The size of Weir's own data, measured at most once a minute.</summary>
public sealed class DataFootprintTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _home.Dispose();

    private void Write(string relativePath, int bytes)
    {
        var path = _home.Join(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public async Task It_adds_up_every_file_under_the_folder_however_deep()
    {
        Write("data/weir.sqlite3", 1000);
        Write("logs/weir.log", 200);
        Write("artwork/posters/a.jpg", 30);

        var footprint = new DataFootprint(_home.Path, _time);

        Assert.Equal(1230, await footprint.BytesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_takes_no_room()
    {
        var footprint = new DataFootprint(_home.Join("missing"), _time);

        Assert.Equal(0, await footprint.BytesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_caller_that_arrives_while_the_folder_is_being_walked_shares_that_walk_even_after_a_minute()
    {
        var walks = 0;
        using var release = new ManualResetEventSlim();
        var footprint = new DataFootprint(_home.Path, _time, _ =>
        {
            Interlocked.Increment(ref walks);
            release.Wait();
            return 77;
        });

        var first = footprint.BytesAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = footprint.BytesAsync(CancellationToken.None);
        release.Set();

        Assert.Equal(new long[] { 77, 77 }, await Task.WhenAll(first, second));
        Assert.Equal(1, walks);
    }

    [Fact]
    public async Task The_answer_is_kept_for_a_minute_and_then_measured_again()
    {
        Write("data/weir.sqlite3", 100);
        var footprint = new DataFootprint(_home.Path, _time);
        Assert.Equal(100, await footprint.BytesAsync(CancellationToken.None));

        Write("data/more.bin", 50);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(100, await footprint.BytesAsync(CancellationToken.None));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(150, await footprint.BytesAsync(CancellationToken.None));
    }
}
