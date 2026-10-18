using System.Security.Cryptography;
using System.Text;

namespace Weir.ArrRecorder;

/// <summary>
/// A small, valid torrent for a file nobody has, so a download client has something to list and the manager something to track.
/// Its tracker does not exist; nothing is ever downloaded.
/// </summary>
internal static class TorrentFile
{
    private const int PieceLength = 262_144;
    private const int Length = 524_288;

    /// <summary>The .torrent file for a single file called <paramref name="name"/>.</summary>
    public static byte[] Build(string name)
    {
        var pieces = new List<byte>();
#pragma warning disable CA5350 // The BitTorrent format fixes SHA-1 for its piece hashes; nothing here is a security boundary.
        for (var piece = 0; piece < Length / PieceLength; piece++)
        {
            pieces.AddRange(SHA1.HashData(Encoding.ASCII.GetBytes($"{name}:{piece}")));
        }

#pragma warning restore CA5350
        var info = new List<byte>();
        info.AddRange("d"u8.ToArray());
        info.AddRange(Pair("length", Integer(Length)));
        info.AddRange(Pair("name", Text(name)));
        info.AddRange(Pair("piece length", Integer(PieceLength)));
        info.AddRange(Pair("pieces", Bytes([.. pieces])));
        info.AddRange("e"u8.ToArray());

        var file = new List<byte>();
        file.AddRange("d"u8.ToArray());
        file.AddRange(Pair("announce", Text("http://tracker.invalid/announce")));
        file.AddRange(Pair("info", [.. info]));
        file.AddRange("e"u8.ToArray());
        return [.. file];
    }

    private static byte[] Pair(string key, byte[] value) => [.. Text(key), .. value];

    private static byte[] Integer(int value) => Encoding.ASCII.GetBytes($"i{value}e");

    private static byte[] Text(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    private static byte[] Bytes(byte[] value) => [.. Encoding.ASCII.GetBytes($"{value.Length}:"), .. value];
}
