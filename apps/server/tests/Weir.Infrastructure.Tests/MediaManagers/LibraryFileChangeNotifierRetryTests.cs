using System.Net;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// Telling a manager that a library file changed when the manager does not answer, does not know the file, or has already been told:
/// each is retried, left alone or counted once, and none of them is allowed to fail the swap that caused it.
/// </summary>
public sealed class LibraryFileChangeNotifierRetryTests
{
    private const string Manifest = "/api/integrations/external/manifest";
    private const string FileChanged = "/api/integrations/external/file-changed";
    private const string WithCapability = """{"libraries":[],"capabilities":["movies","tv","external-file-changed"]}""";

    private static LibraryFileChangeNotifier Notifier(MediaManagerFixture fixture, Func<int, CancellationToken, Task>? delay = null) =>
        new(fixture.Connections, fixture.Store.Database, new SqliteActivityWriter(fixture.Store.Database), fixture.Store.Clock, delay: delay);

    private static Func<int, CancellationToken, Task> Recording(List<int> attempts) => (attempt, _) =>
    {
        attempts.Add(attempt);
        return Task.CompletedTask;
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_change_with_no_file_path_is_refused(string path)
    {
        using var fixture = new MediaManagerFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => Notifier(fixture).NotifyAsync(new LibraryFileChange("movie", path)));
    }

    [Fact]
    public async Task A_change_with_nothing_given_is_refused()
    {
        using var fixture = new MediaManagerFixture();

        await Assert.ThrowsAsync<ArgumentNullException>(() => Notifier(fixture).NotifyAsync(null!));
    }

    [Fact]
    public async Task A_manager_that_does_not_answer_is_asked_three_times_with_a_longer_wait_each_time_and_then_warned_about()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("radarr");
        fixture.Http.Json(HttpMethod.Get, "/api/v3/rootfolder", "unavailable", HttpStatusCode.ServiceUnavailable);
        var waits = new List<int>();

        await Notifier(fixture, Recording(waits)).NotifyAsync(new LibraryFileChange("movie", "/media/Solaris/f.mkv"));

        Assert.Equal(3, fixture.Http.RequestsTo(HttpMethod.Get, "/api/v3/rootfolder").Count);
        Assert.Equal([1, 2], waits);
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'library.file_change_notify_warning'"));
        Assert.Empty(fixture.Http.RequestsTo(HttpMethod.Post, "/api/v3/command"));
    }

    [Fact]
    public async Task A_manager_whose_file_list_cannot_be_read_is_asked_again_and_then_warned_about()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("radarr");
        fixture.Http
            .Json(HttpMethod.Get, "/api/v3/rootfolder", "[]")
            .Json(HttpMethod.Get, "/api/v3/movie", "unavailable", HttpStatusCode.ServiceUnavailable);
        var waits = new List<int>();

        await Notifier(fixture, Recording(waits)).NotifyAsync(new LibraryFileChange("movie", "/media/Solaris/f.mkv"));

        Assert.Equal([1, 2], waits);
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'library.file_change_notify_warning'"));
    }

    [Fact]
    public async Task A_manager_that_comes_back_on_a_later_try_is_told_and_no_warning_is_kept()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("radarr");
        var tries = 0;
        fixture.Http
            .Route(HttpMethod.Get, "/api/v3/rootfolder", _ => ++tries < 2
                ? FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable, "unavailable")
                : FakeManagerHttp.Response(HttpStatusCode.OK, "[]"))
            .Json(HttpMethod.Get, "/api/v3/movie", """[{"id":7,"title":"Solaris","movieFile":{"path":"/media/Solaris/f.mkv"}}]""")
            .Json(HttpMethod.Post, "/api/v3/command", """{"id":1}""", HttpStatusCode.Created);
        var waits = new List<int>();

        await Notifier(fixture, Recording(waits)).NotifyAsync(new LibraryFileChange("movie", "/media/Solaris/f.mkv"));

        Assert.Equal([1], waits);
        Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, "/api/v3/command"));
        Assert.Equal(1, await fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'library.file_change_notified'"));
        Assert.Equal(0, await fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE result = 'warning'"));
    }

    [Fact]
    public async Task A_cancelled_change_does_not_wait_out_the_pause_between_tries()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("radarr");
        using var cancelled = new CancellationTokenSource();
        fixture.Http.Route(HttpMethod.Get, "/api/v3/rootfolder", _ =>
        {
            cancelled.Cancel();
            return FakeManagerHttp.Response(HttpStatusCode.ServiceUnavailable, "unavailable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Notifier(fixture).NotifyAsync(new LibraryFileChange("movie", "/media/Solaris/f.mkv"), cancelled.Token));

        Assert.Equal(0, await fixture.Store.Scalar("SELECT count(*) FROM activity_events"));
    }

    [Fact]
    public async Task Deluno_saying_it_does_not_track_the_file_is_left_alone_and_nothing_is_recorded()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("deluno");
        fixture.Http
            .Json(HttpMethod.Get, Manifest, WithCapability)
            .Json(HttpMethod.Post, FileChanged, """{"error":"not tracked"}""", HttpStatusCode.NotFound);
        var waits = new List<int>();

        await Notifier(fixture, Recording(waits)).NotifyAsync(new LibraryFileChange("movie", "/media/f.mkv"));

        Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, FileChanged));
        Assert.Empty(waits);
        Assert.Equal(0, await fixture.Store.Scalar("SELECT count(*) FROM activity_events"));
    }

    [Fact]
    public async Task Deluno_told_about_one_file_twice_in_a_row_is_told_once_until_the_window_has_passed()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("deluno");
        fixture.Http
            .Json(HttpMethod.Get, Manifest, WithCapability)
            .Json(HttpMethod.Post, FileChanged, """{"path":"/media/f.mkv","coalesced":false,"titles":[]}""", HttpStatusCode.Accepted);
        var notifier = Notifier(fixture);
        var change = new LibraryFileChange("movie", "/media/f.mkv");

        await notifier.NotifyAsync(change);
        await notifier.NotifyAsync(change);
        Assert.Single(fixture.Http.RequestsTo(HttpMethod.Post, FileChanged));

        fixture.Store.Clock.Advance(LibraryFileChangeRules.CoalesceWindow + TimeSpan.FromSeconds(1));
        await notifier.NotifyAsync(new LibraryFileChange("movie", "/media/other.mkv"));
        await notifier.NotifyAsync(change);

        Assert.Equal(3, fixture.Http.RequestsTo(HttpMethod.Post, FileChanged).Count);
    }

    [Fact]
    public async Task A_manager_that_is_not_one_the_change_belongs_to_is_not_asked_about_it()
    {
        using var fixture = new MediaManagerFixture();
        await fixture.AddConnectionAsync("sonarr");
        fixture.Http.Json(HttpMethod.Get, "/api/v3/rootfolder", "[]");

        await Notifier(fixture).NotifyAsync(new LibraryFileChange("movie", "/media/f.mkv"));

        Assert.Empty(fixture.Http.Requests);
    }
}
