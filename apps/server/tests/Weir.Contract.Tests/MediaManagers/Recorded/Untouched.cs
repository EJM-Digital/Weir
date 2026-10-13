using System.Security.Cryptography;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>A file in a linked folder as it was, to show afterwards that Weir never wrote, moved or removed it.</summary>
internal sealed class Untouched
{
    private readonly string _path;
    private readonly string _content;
    private readonly DateTime _modified;

    private Untouched(string path)
    {
        _path = path;
        _content = Hash(path);
        _modified = File.GetLastWriteTimeUtc(path);
    }

    public static Untouched Of(string path) => new(path);

    public void AssertStill()
    {
        Assert.True(File.Exists(_path), $"{_path} is gone from the linked folder");
        Assert.Equal(_content, Hash(_path));
        Assert.Equal(_modified, File.GetLastWriteTimeUtc(_path));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
