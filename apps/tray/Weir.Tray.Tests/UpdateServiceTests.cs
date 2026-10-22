using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The tray's update service on Velopack's own update manager, installed into a folder of the test's own: a release source
/// that serves one version, a real package on disk and a stand-in for Velopack's Update program that records what it is told.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _install = TempDirectory.Create();
    private readonly List<string> _log = [];
    private readonly List<int> _exits = [];

    public void Dispose() => _install.Dispose();

    [Fact]
    public async Task A_copy_of_Weir_that_is_not_installed_looks_for_nothing_and_says_so_in_plain_words()
    {
        var service = Service(new UpdateManager(new FeedSource(null), new UpdateOptions(), VelopackLocator.CreateDefaultForPlatform(new HostProcess(), null)));

        Assert.False(service.IsInstalled);
        Assert.Null(service.FindUpdateLeftWaiting());
        var check = await service.CheckForUpdateAsync();
        Assert.NotNull(check.Failure);
        Assert.Contains("Update check failed", Assert.Single(_log, line => line.StartsWith("Update check failed", StringComparison.Ordinal)));
        Assert.False(service.HasPendingUpdate);
    }

    [Fact]
    public async Task A_check_that_finds_nothing_leaves_nothing_pending()
    {
        var service = Service(Manager(new FeedSource("1.0.0")));

        Assert.True(service.IsInstalled);
        Assert.Equal(UpdateOutcome.NothingToDo, await service.CheckForUpdateAsync());
        Assert.False(service.HasPendingUpdate);
        Assert.Null(service.PendingVersion);
        Assert.Contains("No update available.", _log);
    }

    [Fact]
    public async Task A_newer_release_is_found_downloaded_and_checked_before_it_counts_as_downloaded()
    {
        var source = new FeedSource("1.1.0");
        var service = Service(Manager(source));

        Assert.Equal(UpdateOutcome.NothingToDo, await service.DownloadUpdateAsync());

        Assert.Equal(UpdateOutcome.Succeeded, await service.CheckForUpdateAsync());
        Assert.True(service.HasPendingUpdate);
        Assert.Equal("1.1.0", service.PendingVersion);
        Assert.False(service.IsDownloaded);

        Assert.Equal(UpdateOutcome.Succeeded, await service.DownloadUpdateAsync());
        Assert.True(service.IsDownloaded);
        Assert.Equal(100, service.DownloadProgress);
        Assert.Contains("Update downloaded successfully.", _log);

        // Looking again starts over: the earlier download is not trusted for what a later check finds.
        Assert.Equal(UpdateOutcome.NothingToDo, await Service(Manager(new FeedSource("1.0.0"))).CheckForUpdateAsync());
    }

    [Fact]
    public async Task A_check_that_finds_nothing_after_one_that_found_something_forgets_it()
    {
        var source = new FeedSource("1.1.0");
        var service = Service(Manager(source));
        await service.CheckForUpdateAsync();
        await service.DownloadUpdateAsync();

        source.Offered = "1.0.0";

        Assert.Equal(UpdateOutcome.NothingToDo, await service.CheckForUpdateAsync());
        Assert.False(service.HasPendingUpdate);
        Assert.False(service.IsDownloaded);
    }

    [Fact]
    public async Task A_download_that_fails_is_reported_and_leaves_the_update_not_downloaded()
    {
        var source = new FeedSource("1.1.0") { PackageBytes = Encoding.UTF8.GetBytes("a different package than the feed describes") };
        var service = Service(Manager(source));
        await service.CheckForUpdateAsync();

        var download = await service.DownloadUpdateAsync();

        Assert.NotNull(download.Failure);
        Assert.False(service.IsDownloaded);
        Assert.Contains(_log, line => line.StartsWith("Update download failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applying_before_anything_is_downloaded_does_nothing()
    {
        var service = Service(Manager(new FeedSource("1.1.0")));
        await service.CheckForUpdateAsync();

        service.ApplyAndExit();
        service.ApplyAndRestart();

        Assert.Empty(_exits);
        Assert.DoesNotContain(_log, line => line.StartsWith("Applying", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_downloaded_update_is_handed_to_Velopack_silently_and_this_process_ends(bool restart)
    {
        var updater = new UpdateProgram(_install.Path);
        var service = Service(Manager(new FeedSource("1.1.0"), updater));
        await service.CheckForUpdateAsync();
        await service.DownloadUpdateAsync();

        if (restart)
        {
            service.ApplyAndRestart();
        }
        else
        {
            service.ApplyAndExit();
        }

        Assert.Equal([0], _exits);
        var told = updater.WaitForArguments();
        Assert.Contains("--silent", told);
        Assert.Equal(restart, !told.Contains("--norestart", StringComparison.Ordinal));
        Assert.Contains(_log, line => line.Contains(restart ? "and restarting" : "and exiting", StringComparison.Ordinal));
    }

    [Fact]
    public void An_update_left_waiting_by_an_earlier_run_is_found_and_installed_with_a_restart()
    {
        var updater = new UpdateProgram(_install.Path);
        var service = Service(Manager(new FeedSource("1.0.0"), updater));
        Assert.Null(service.FindUpdateLeftWaiting());
        service.ApplyLeftWaitingUpdateAndRestart();
        Assert.Empty(_log);

        Package.Write(_install.Path, "1.2.0");

        Assert.Equal("1.2.0", service.FindUpdateLeftWaiting());
        service.ApplyLeftWaitingUpdateAndRestart();
        var told = updater.WaitForArguments();
        Assert.Contains("--silent", told);
        Assert.DoesNotContain("--norestart", told, StringComparison.Ordinal);
        Assert.Contains("Applying update v1.2.0 left waiting, and restarting...", _log);
        Assert.Empty(_exits);
    }

    private UpdateService Service(UpdateManager manager) => new(manager, _log.Add, _exits.Add);

    private UpdateManager Manager(FeedSource source, UpdateProgram? updater = null) =>
        new(source, new UpdateOptions(), new TestVelopackLocator(
            "Weir", "1.0.0", Path.Combine(_install.Path, "packages"), _install.Path, _install.Path,
            updater?.Path ?? Path.Combine(_install.Path, "Update.exe"), "win", null, null, Path.Combine(_install.Path, "Weir.exe")));

    /// <summary>A release source serving one version of Weir, whose package is a real one on disk.</summary>
    private sealed class FeedSource(string? offered) : IUpdateSource
    {
        public string? Offered { get; set; } = offered;

        public byte[]? PackageBytes { get; init; }

        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            if (Offered is null)
            {
                throw new InvalidOperationException("No release source.");
            }
            var package = Package.Bytes(Offered);
#pragma warning disable CA5350 // Velopack's release feed carries a SHA1 beside the SHA256; the feed has to name both.
#pragma warning disable CA5350 // Velopack's release feed carries a SHA1 beside the SHA256; the feed has to name both.
#pragma warning disable CA5350 // Velopack's release feed carries a SHA1 beside the SHA256; the feed has to name both.
            return Task.FromResult(VelopackAssetFeed.FromJson(
                $$"""{"Assets":[{"PackageId":"Weir","Version":"{{Offered}}","Type":"Full","FileName":"Weir-{{Offered}}-full.nupkg","SHA1":"{{Convert.ToHexString(SHA1.HashData(package))}}","SHA256":"{{Convert.ToHexString(SHA256.HashData(package))}}","Size":{{package.Length}}}]}"""));
#pragma warning restore CA5350
#pragma warning restore CA5350
#pragma warning restore CA5350
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            File.WriteAllBytes(localFile, PackageBytes ?? Package.Bytes(releaseEntry.Version.ToString()));
            progress(100);
            return Task.CompletedTask;
        }
    }

    /// <summary>This test process as Velopack sees it: it was not started by Velopack's installer, so it is not an install.</summary>
    private sealed class HostProcess : IProcessImpl
    {
        public string GetCurrentProcessPath() => Environment.ProcessPath!;

        public uint GetCurrentProcessId() => (uint)Environment.ProcessId;

        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow) => throw new NotSupportedException();

        public void Exit(int exitCode) => throw new NotSupportedException();
    }

    /// <summary>The package Velopack keeps for a version: a zip with the nuspec that names it.</summary>
    private static class Package
    {
        internal static byte[] Bytes(string version)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("Weir.nuspec").Open());
                writer.Write($"""<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Weir</id><version>{version}</version><title>Weir</title></metadata></package>""");
            }
            return stream.ToArray();
        }

        internal static void Write(string installFolder, string version)
        {
            var packages = Path.Combine(installFolder, "packages");
            Directory.CreateDirectory(packages);
            File.WriteAllBytes(Path.Combine(packages, $"Weir-{version}-full.nupkg"), Bytes(version));
        }
    }

    /// <summary>Stands in for Velopack's Update program: a script that writes down the arguments it was started with.</summary>
    private sealed class UpdateProgram
    {
        private readonly string _record;

        internal UpdateProgram(string folder)
        {
            Path = System.IO.Path.Combine(folder, "Update.cmd");
            _record = System.IO.Path.Combine(folder, "update-arguments.txt");
            File.WriteAllText(Path, $"@echo off\r\necho %* > \"{_record}\"\r\n");
        }

        internal string Path { get; }

        internal string WaitForArguments()
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(_record) && new FileInfo(_record).Length > 0, Ceiling));
            return File.ReadAllText(_record);
        }
    }
}
