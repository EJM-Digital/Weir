using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using static Weir.Contract.Tests.MediaManagers.DelunoSyncRig;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Deluno's own replies (recorded in its server code, rc.30.32) to the calls Weir makes of it, served to Weir by a fake Deluno: where
/// downloads land, and the refusals a key or a missing sign-in meets. Weir must read every member it uses, and answer each status with
/// a plain sentence, no crash, and the right retry or stop. Calls Weir does not make (the hand-off list, import-original, send-again)
/// have nothing here to replay; the README beside the recordings says which.
/// </summary>
[ContractArea("media_managers")]
public sealed class DelunoRepliesTests
{
    private const string SetupRoute = $"{WeirClient.Api}/processing/manager-setup";

    // --- where downloads land ------------------------------------------------------------------------

    [Fact]
    public async Task The_downloads_and_output_folders_Deluno_recorded_for_a_library_are_read_and_judged_against_the_workflows_folders()
    {
        await using var rig = await Rig.StartAsync();
        rig.Fake.Serve(DelunoSide.Reply("reply-download-destinations-libraries"), rig.Placeholders);

        var check = await rig.SetupCheckAsync();

        Assert.Equal(
            [
                ("ok", $"{rig.Label}'s films library finishes downloads in {rig.Downloads}, inside this workflow's watched folder."),
                ("ok", $"{rig.Label} picks up cleaned files from {rig.Ready}, the folder this workflow writes to."),
            ],
            Lines(check));
        Assert.Equal(rig.Downloads, (string)check["suggested_watched_folder"]!);
        Assert.Equal(rig.Ready, (string)check["suggested_output_folder"]!);
        Assert.True((bool)check["ready"]!);
        // One question, for the library the workflow came from, with the connection's key and nothing else.
        var asked = Assert.Single(rig.Fake.RequestsTo("GET", DestinationsPath));
        Assert.Equal([rig.Placeholders.Library("films")], asked.Query["libraryId"]);
        Assert.Equal(rig.Fake.ApiKey, asked.Header("X-Api-Key"));
        Assert.Equal(["libraryId"], asked.Query.Keys);
    }

    [Fact]
    public async Task The_path_mappings_of_the_processor_connection_Deluno_recorded_turn_its_folders_into_Weirs_view()
    {
        // Deluno's data root is "/downloads/box" here, where its recorded mapping sends /downloads to D:/Downloads.
        await using var rig = await Rig.StartAsync(delunoDataRoot: "/downloads/box");
        rig.Fake.Serve(DelunoSide.Reply("reply-download-destinations-libraries"), rig.Placeholders);

        var check = await rig.SetupCheckAsync();

        // Weir writes a mapped path with this machine's separators after the mapped folder; the folder it lands in is what is held.
        Assert.Equal("D:/Downloads/box/films-downloads", Slashed((string)check["suggested_watched_folder"]!));
        var downloads = Assert.Single(Lines(check), line => line.Text.Contains("finishes downloads", StringComparison.Ordinal));
        Assert.Equal("problem", downloads.State);
        Assert.Contains("/downloads/box/films-downloads (Weir sees it as D:/Downloads/box/films-downloads)", Slashed(downloads.Text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_library_Deluno_recorded_is_read_when_workflows_are_set_up_and_one_with_no_processor_connection_does_no_harm()
    {
        using var folder = new TemporaryFolder();
        var placeholders = new DelunoPlaceholders(folder.Path);
        using var deluno = RigFake(placeholders, folder.Path);
        deluno.Serve(DelunoSide.Reply("reply-download-destinations-libraries"), placeholders, exchanges: [0]);
        await using var server = await WeirServer.StartNewAsync(SyncOn);
        using var admin = await server.CreateAdminClientAsync();
        Directory.CreateDirectory(Path.Join(folder.Path, "films-downloads"));
        Directory.CreateDirectory(Path.Join(folder.Path, "films-ready"));

        await ConnectAsync(admin, deluno);

        var workflows = await SyncedAsync(admin, expected: 1);
        var movies = Movies(workflows);
        Assert.Equal(placeholders.Library("films"), (string)movies["discovered_library_key"]!);
        Assert.Equal(Slashed(Path.Join(folder.Path, "films-downloads")), Slashed((string)movies["watched_folder"]!));
        Assert.Equal(Slashed(Path.Join(folder.Path, "films-ready")), Slashed((string)movies["output_folder"]!));
        // The shows library Deluno does not hand to a processor (standard import, no processor connection) is not set up.
        Assert.Null(Assert.Single(workflows, row => (string)row!["media_type"]! == "tv")!["discovered_library_key"]);
        Assert.Single(deluno.RequestsTo("GET", DestinationsPath));
    }

    [Fact]
    public async Task A_Deluno_with_no_library_and_one_that_does_not_know_the_library_asked_for_are_both_read_without_a_crash()
    {
        await using var rig = await Rig.StartAsync();
        var recording = DelunoSide.Reply("reply-download-destinations-empty");

        rig.Fake.Serve(recording, rig.Placeholders, exchanges: [0], anyQuery: true);
        var empty = await rig.SetupCheckAsync();
        // 200 with no libraries: the library the workflow came from is gone.
        Assert.Equal(
            [("unverified", $"{rig.Label} no longer has the library this workflow came from, so Weir can't check where its downloads go. " +
                $"Remove this workflow if the library is gone for good, or unlink it from {rig.Label} to keep it as a Weir-only workflow.")],
            Lines(empty));

        rig.Fake.Serve(recording, rig.Placeholders, exchanges: [1], anyQuery: true);
        var unknown = await rig.SetupCheckAsync();
        // 404 with no body is how Weir tells a Deluno that predates the route; it keeps the manifest's lines and adds a note.
        Assert.Contains(
            ("note", $"Deluno 1.0.0-rc.23 or later lets Weir check where its download clients save and how its path mappings apply."),
            Lines(unknown));
    }

    // --- refusals ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_key_Deluno_refuses_for_a_missing_Imports_permission_is_told_to_get_one_in_plain_words()
    {
        await using var rig = await Rig.StartAsync();
        rig.Fake.Serve(DelunoSide.Reply("reply-key-without-imports-scope"), rig.Placeholders, exchanges: [1], anyQuery: true);

        var check = await rig.SetupCheckAsync();

        var line = Assert.Single(Lines(check), candidate => candidate.Text.Contains("Imports", StringComparison.Ordinal));
        Assert.Equal(
            ("unverified", $"The API key Weir uses for {rig.Label} can't read where downloads go. Give it the Imports permission: " +
                "in Deluno, open System › API Access and create a key with Media automation access (it includes Imports), then save that key under Setup › Connections › Media managers in Weir."),
            line);
    }

    [Fact]
    public async Task The_other_403_shape_Deluno_recorded_is_read_as_the_same_missing_permission()
    {
        await using var rig = await Rig.StartAsync();
        // Deluno recorded this shape for its write routes; it is the refusal a key without the permission meets on any of them.
        rig.Fake.Serve(DelunoSide.Reply("reply-key-without-imports-scope"), rig.Placeholders, exchanges: [2], anyQuery: true, asRoute: ("GET", DestinationsPath));

        var check = await rig.SetupCheckAsync();

        Assert.Contains(Lines(check), line => line.State == "unverified" && line.Text.StartsWith($"The API key Weir uses for {rig.Label} can't read where downloads go.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_with_no_sign_in_is_answered_401_and_Weir_says_its_key_was_refused()
    {
        await using var rig = await Rig.StartAsync();
        rig.Fake.Serve(DelunoSide.Reply("reply-no-sign-in"), rig.Placeholders, exchanges: [1], anyQuery: true);

        var check = await rig.SetupCheckAsync();

        Assert.Contains(
            ("unverified", $"{rig.Label} refused Weir's API key, so it could not say where its downloads are saved. Check the key in Setup › Connections › Media managers and save it again."),
            Lines(check));
    }

    // --- the one permission Weir needs ---------------------------------------------------------------

    [Fact]
    public async Task A_key_with_only_the_Imports_permission_reads_where_downloads_land_and_is_never_asked_for_Read()
    {
        await using var rig = await Rig.StartAsync();
        rig.Fake.Serve(DelunoSide.Reply("reply-key-with-imports-scope-only"), rig.Placeholders, exchanges: [1], anyQuery: true);

        var check = await rig.SetupCheckAsync();

        // Deluno answered 200: the read worked, so Weir has no complaint about the key; it only finds the library is not in the answer.
        Assert.Single(Lines(check));
        Assert.StartsWith($"{rig.Label} no longer has the library this workflow came from", Lines(check)[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Lines(check), line => line.Text.Contains("permission", StringComparison.OrdinalIgnoreCase));
        var asked = Assert.Single(rig.Fake.RequestsTo("GET", DestinationsPath));
        Assert.Equal(rig.Fake.ApiKey, asked.Header("X-Api-Key"));
    }

    // --- steps ----------------------------------------------------------------------------------------

    private static string Slashed(string path) => path.Replace('\\', '/');

    private static List<(string State, string Text)> Lines(JsonObject check) =>
        [.. check["lines"]!.AsArray().Select(line => ((string)line!["state"]!, (string)line["text"]!))];

    /// <summary>A fake Deluno whose manifest lists the Movies library the recordings call films, set to Refine before import, and a Shows library that is not.</summary>
    private static FakeManager RigFake(DelunoPlaceholders placeholders, string dataRoot)
    {
        var root = dataRoot.Replace('\\', '/');
        return FakeManager.StartDeluno(
        [
            new JsonObject
            {
                ["id"] = placeholders.Library("films"),
                ["name"] = "films",
                ["mediaType"] = "movies",
                ["rootPath"] = "/library/films",
                ["importWorkflow"] = "refine-before-import",
                ["processorOutputPath"] = $"{root}/films-ready",
                ["downloadsPath"] = $"{root}/films-downloads",
            },
            new JsonObject
            {
                ["id"] = placeholders.Library("shows"),
                ["name"] = "shows",
                ["mediaType"] = "tv",
                ["rootPath"] = "/library/shows",
                ["importWorkflow"] = "standard",
                ["processorOutputPath"] = string.Empty,
                ["downloadsPath"] = $"{root}/shows-downloads",
            },
        ]);
    }

    /// <summary>A server, a workflow's folders on this machine, and a fake Deluno the recordings are served from.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TemporaryFolder _folder;
        private readonly WeirServer _server;
        private readonly WeirClient _admin;

        private Rig(TemporaryFolder folder, FakeManager fake, DelunoPlaceholders placeholders, WeirServer server, WeirClient admin, string label)
        {
            _folder = folder;
            Fake = fake;
            Placeholders = placeholders;
            _server = server;
            _admin = admin;
            Label = label;
        }

        public FakeManager Fake { get; }

        public DelunoPlaceholders Placeholders { get; }

        /// <summary>What Weir calls the fake Deluno in its sentences.</summary>
        public string Label { get; }

        public string Downloads => Path.Join(_folder.Path, "films-downloads").Replace('\\', '/');

        public string Ready => Path.Join(_folder.Path, "films-ready").Replace('\\', '/');

        /// <summary>
        /// <paramref name="delunoDataRoot"/> is the folder Deluno's recorded <c>&lt;data-root&gt;</c> stands for; it is this machine's own
        /// folder unless a test wants Deluno's folders to be ones its path mapping covers.
        /// </summary>
        public static async Task<Rig> StartAsync(string? delunoDataRoot = null)
        {
            var folder = new TemporaryFolder();
            Directory.CreateDirectory(Path.Join(folder.Path, "films-downloads"));
            Directory.CreateDirectory(Path.Join(folder.Path, "films-ready"));
            var placeholders = new DelunoPlaceholders(delunoDataRoot ?? folder.Path);
            var fake = RigFake(placeholders, delunoDataRoot ?? folder.Path);
            var server = await WeirServer.StartNewAsync(new Dictionary<string, string>());
            var admin = await server.CreateAdminClientAsync();
            var created = await ManagerConnections.CreateAsync(admin, new JsonObject { ["base_url"] = fake.BaseUrl, ["api_key"] = fake.ApiKey });
            Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
            return new Rig(folder, fake, placeholders, server, admin, $"Deluno on {new Uri(fake.BaseUrl).Host}");
        }

        /// <summary>What the Manager check says of a movie workflow watching and writing to the folders above.</summary>
        public async Task<JsonObject> SetupCheckAsync()
        {
            var answer = await _admin.GetAsync(
                SetupRoute, ("media_type", "movie"), ("watched_folder", Path.Join(_folder.Path, "films-downloads")), ("output_folder", Path.Join(_folder.Path, "films-ready")));
            Assert.True(answer.Status == HttpStatusCode.OK, answer.ToString());
            return Assert.Single(answer.Fields["managers"]!.AsArray())!.AsObject();
        }

        public async ValueTask DisposeAsync()
        {
            _admin.Dispose();
            await _server.DisposeAsync();
            Fake.Dispose();
            _folder.Dispose();
        }
    }
}
