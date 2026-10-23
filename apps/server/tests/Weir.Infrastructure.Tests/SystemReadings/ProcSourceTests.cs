using Weir.Infrastructure.SystemReadings;

namespace Weir.Infrastructure.Tests.SystemReadings;

/// <summary>The real file reads behind the Linux readings, on whatever machine the tests run.</summary>
public sealed class ProcSourceTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly ProcSource _source = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void A_file_or_a_folder_exists_and_a_name_with_nothing_behind_it_does_not()
    {
        File.WriteAllText(_home.Join("meminfo"), "MemTotal: 1 kB");
        Directory.CreateDirectory(_home.Join("sys"));

        Assert.True(_source.Exists(_home.Join("meminfo")));
        Assert.True(_source.Exists(_home.Join("sys")));
        Assert.False(_source.Exists(_home.Join("missing")));
    }

    [Fact]
    public void A_file_is_read_and_one_that_cannot_be_read_is_nothing()
    {
        File.WriteAllText(_home.Join("meminfo"), "MemTotal: 1 kB");
        Directory.CreateDirectory(_home.Join("sys"));

        Assert.Equal("MemTotal: 1 kB", _source.ReadAllText(_home.Join("meminfo")));
        Assert.Null(_source.ReadAllText(_home.Join("missing")));
        Assert.Null(_source.ReadAllText(_home.Join("sys")));
    }
}
