using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Weir.Api.Tests.TestSupport;

/// <summary>
/// Windows Firewall asks whoever runs the tests whether to let <c>testhost.exe</c> or <c>node.exe</c> through whenever a test
/// listens on an address other than loopback, and the answer is a dialog on the desktop. Tests and their helpers listen on
/// 127.0.0.1, ::1 or localhost only. This scans the server tests, the tray tests and the Node scenario tests for the ways to
/// listen on every interface. A line that only binds (and never listens) and has to name a wildcard says why with
/// <c>loopback-guard:</c> in a trailing comment. Product code is not scanned: Weir's default listen address and the tray's
/// every-interface port probe are deliberate, and tests reach them through a seam that names loopback.
/// </summary>
public sealed partial class TestsListenOnLoopbackOnlyTests
{
    private const string Exemption = "loopback-guard:";

    [Fact]
    public void No_test_or_test_helper_listens_on_an_address_other_than_loopback()
    {
        var offenders = new List<string>();
        foreach (var path in SourcesToScan())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsComment(line) || line.Contains(Exemption, StringComparison.Ordinal) || !ListensBeyondLoopback(line))
                {
                    continue;
                }

                offenders.Add($"{Path.GetRelativePath(RepositoryRoot(), path)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "These lines listen beyond loopback, which makes Windows Firewall prompt:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("var l = new TcpListener(IPAddress.Any, 0);", true)]
    [InlineData("var l = new TcpListener(IPAddress.IPv6Any, 0);", true)]
    [InlineData("socket.Bind(new IPEndPoint(IPAddress.Any, port));", true)]
    [InlineData("kestrel.ListenAnyIP(port);", true)]
    [InlineData("listener.Prefixes.Add($\"http://+:{port}/\");", true)]
    [InlineData("listener.Prefixes.Add($\"http://*:{port}/\");", true)]
    [InlineData("listener.Prefixes.Add($\"http://0.0.0.0:{port}/\");", true)]
    [InlineData("builder.WebHost.UseUrls(\"http://*:8080\");", true)]
    [InlineData("RealServerProcess.Start(_home, \"0.0.0.0\", port);", true)]
    [InlineData("server.listen(0, () => resolve());", true)]
    [InlineData("server.listen(port, resolve);", true)]
    [InlineData("var l = new TcpListener(IPAddress.Loopback, 0);", false)]
    [InlineData("var l = new TcpListener(IPAddress.IPv6Loopback, 0);", false)]
    [InlineData("listener.Prefixes.Add($\"http://127.0.0.1:{port}/\");", false)]
    [InlineData("listener.Prefixes.Add($\"http://localhost:{port}/\");", true)]
    [InlineData("RealServerProcess.Start(_home, \"127.0.0.1\", port);", false)]
    [InlineData("server.listen(0, \"127.0.0.1\", resolve);", false)]
    [InlineData("server.listen(port, host, () => resolve());", false)]
    [InlineData("const port = await standIn.listen();", false)]
    public void The_scan_tells_a_listener_beyond_loopback_from_one_on_loopback(string line, bool beyondLoopback)
    {
        Assert.Equal(beyondLoopback, ListensBeyondLoopback(line));
    }

    private static bool ListensBeyondLoopback(string line) =>
        WildcardAddress().IsMatch(line)
        || WildcardPrefix().IsMatch(line)
        || WildcardServerStart().IsMatch(line)
        || NodeListenWithoutHost().IsMatch(line);

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourcesToScan()
    {
        var root = RepositoryRoot();
        var scripts = Path.Combine(root, "scripts");
        var dotnet = new[]
        {
            Path.Combine(root, "apps", "server", "tests"),
            Path.Combine(root, "apps", "tray", "Weir.Tray.Tests"),
            Path.Combine(root, "apps", "tray", "Weir.Tray.StandInServer"),
        }.SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories));
        var node = Directory.EnumerateFiles(scripts, "scenarios-*.test.mjs")
            .Concat(Directory.EnumerateFiles(Path.Combine(scripts, "scenarios-ci"), "*.mjs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(scripts, "scenarios-ci", "with-deluno", "recorder"), "*.mjs"));
        return dotnet
            .Concat(node)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "obj" or "bin"))
            .Where(path => !path.EndsWith(nameof(TestsListenOnLoopbackOnlyTests) + ".cs", StringComparison.Ordinal));
    }

    // apps/server/tests/Weir.Api.Tests/TestSupport/<this file> -> the repository root.
    private static string RepositoryRoot([CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", "..", "..", ".."));

    [GeneratedRegex(@"IPAddress\.(Any|IPv6Any)\b|ListenAnyIP\(")]
    private static partial Regex WildcardAddress();

    // An HttpListener prefix names a host name or a wildcard, and http.sys then listens on every address for it: only an IP literal
    // of loopback keeps it on loopback, so "localhost" counts as beyond it.
    [GeneratedRegex(@"(Prefixes\.Add|UseUrls)\(.*http://(?!127\.0\.0\.1[:/]|\[::1\])")]
    private static partial Regex WildcardPrefix();

    [GeneratedRegex(@"RealServerProcess\.Start\([^,]+,\s*""(0\.0\.0\.0|\*|::)""")]
    private static partial Regex WildcardServerStart();

    // Node listens on every interface when no host is given. A host variable or a loopback literal is a stated host.
    [GeneratedRegex(@"\.listen\(\s*(\d+|port)\s*(\)|,\s*(\(|(resolve|done|cb|callback)\b))")]
    private static partial Regex NodeListenWithoutHost();
}
