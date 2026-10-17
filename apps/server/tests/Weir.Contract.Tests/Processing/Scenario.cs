using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// One processing scenario: a server that actually works files (one worker, a webhook secret), its media tools, the three folders
/// a workflow needs, and whatever fakes the scenario starts. Every step it offers goes through the public API; the only things a
/// scenario controls from outside are the ones Weir does not own: the files on disk, the fake ffmpeg and the fake media manager.
/// </summary>
internal sealed partial class Scenario : IAsyncDisposable
{
    public const string WebhookSecret = "contract-webhook-secret-0123456789";
    public const string RemuxKind = "processing.file.remux_pass.v1";
    public const string PassThroughKind = "processing.file.pass_through.v1";
    public const string RejectKind = "processing.file.reject.v1";
    public const string DelunoLibraryKey = "5f2c0a9e";
    public const string DelunoOutputRoot = "/deluno/processed/movies";
    public const string EventsPath = "/api/integrations/processors/events";

    /// <summary>The scan of one workflow's watched folder, which the periodic scan, the folder watcher and a person's "scan now" all queue.</summary>
    public const string ScanKind = "processing.watched_folder.remux_scan_dispatch.v1";

    private const string Api = WeirClient.Api;

    private readonly TemporaryFolder _root = new();
    private readonly List<IDisposable> _owned = [];
    private JsonObject? _sentinel;
    private int _rounds;

    private Scenario(WeirServer server, WeirClient admin, FakeFfmpeg? tools)
    {
        Server = server;
        Admin = admin;
        Tools = tools;
        Folders = Folders.Make(_root.Path);
    }

    public WeirServer Server { get; }

    /// <summary>Signed in as the admin; replaced by a new session after <see cref="RestartServerAsync"/>.</summary>
    public WeirClient Admin { get; private set; }

    /// <summary>The fake ffprobe/ffmpeg; null in a scenario that runs the real ones.</summary>
    public FakeFfmpeg? Tools { get; }

    public Folders Folders { get; }

    /// <summary>The workflow a fake Deluno feeds, whose retries come from the hand-off itself rather than from a scan; null until set up.</summary>
    public int? HandedOffLibraryId { get; private set; }

    /// <summary>The fake tools, which every scenario but the real-ffmpeg ones has.</summary>
    public FakeFfmpeg FakeTools => Tools ?? throw new InvalidOperationException("This scenario runs the real ffmpeg.");

    /// <summary>A working server with the fake ffprobe and ffmpeg, and these extra settings.</summary>
    public static async Task<Scenario> StartAsync(params (string Name, string Value)[] extraSettings)
    {
        var tools = FakeFfmpeg.Install();
        return await StartAsync(tools, tools.Env, extraSettings);
    }

    /// <summary>A working server with the real ffprobe and ffmpeg, and these extra settings.</summary>
    public static Task<Scenario> StartWithRealToolsAsync(params (string Name, string Value)[] extraSettings) => StartAsync(tools: null, RealFfmpeg.Env, extraSettings);

    public async Task RestartServerAsync()
    {
        await Server.RestartAsync();
        Admin.Dispose();
        Admin = Server.CreateClient();
        await Admin.LoginAsync();
    }

    /// <summary>Keeps <paramref name="fake"/> until the scenario ends, then stops it.</summary>
    public T Own<T>(T fake)
        where T : IDisposable
    {
        _owned.Add(fake);
        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await Server.DisposeAsync();
        foreach (var fake in _owned)
        {
            fake.Dispose();
        }

        Tools?.Dispose();
        _root.Dispose();
    }

    private static async Task<Scenario> StartAsync(
        FakeFfmpeg? tools, IReadOnlyDictionary<string, string> toolSettings, (string Name, string Value)[] extraSettings)
    {
        var settings = toolSettings.With(
            ("WEIR_PROCESSING_WORKER_COUNT", "1"),
            ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", WebhookSecret)).With(extraSettings);
        var server = await WeirServer.StartNewAsync(settings);
        try
        {
            return new Scenario(server, await server.CreateAdminClientAsync(), tools);
        }
        catch
        {
            await server.DisposeAsync();
            tools?.Dispose();
            throw;
        }
    }
}
