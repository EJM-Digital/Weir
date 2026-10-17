using System.Globalization;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// A job that runs longer than its lease must never be claimed by a second worker while the first is still running it.
/// <c>WEIR_PROCESSING_JOB_LEASE_SECONDS</c> shortens the lease (30 s is the shortest the server accepts), and a heartbeat
/// renews it roughly every third of the lease while the handler runs; this proves it over a real hand-off with two workers.
/// </summary>
[ContractArea("jobs")]
public sealed class LeaseRenewalTests
{
    [Fact]
    public async Task A_job_longer_than_its_lease_is_never_claimed_twice()
    {
        // Two workers sharing one database. The remux is held until the test lets it go, and the test lets it go once the server says the
        // lease has been renewed past the end it was first given: a lease that is renewed is a lease no second worker can take, and
        // one that is not renewed would be reported at its first end for ever. 30s is the minimum lease the server accepts.
        await using var scenario = await Scenario.StartAsync(
            ("WEIR_PROCESSING_WORKER_COUNT", "2"), ("WEIR_PROCESSING_JOB_LEASE_SECONDS", "30"));
        await scenario.DelunoSetupAsync();
        var release = Path.Combine(scenario.Root, "release-remux");
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { Probe = FakeMedia.Probe(), RemuxReleaseFile = release });
        var source = scenario.WriteRelease("Long.Running.540", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe()));

        await scenario.PostHandoffAsync("handoff-lease-540", source);

        var first = await Poll.UntilAsync(() => LeaseAsync(scenario), "a worker to take the job");
        var renewed = await Poll.UntilAsync(
            async () => await LeaseAsync(scenario) is { } lease && lease.EndsAt > first.EndsAt ? lease : null,
            "the lease to be renewed past the end it was first given");
        Assert.Equal(first.Owner, renewed.Owner);
        await File.WriteAllTextAsync(release, string.Empty);
        await scenario.WaitForHandoffStateAsync("handoff-lease-540", "completed");

        // Only one remux call for this file must ever have started: a second worker claiming the same
        // row mid-write would show up here as two "remux" calls for film.mkv instead of one.
        Assert.Single(await scenario.JobsAsync(Scenario.RemuxKind));
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
    }

    private sealed record Lease(string Owner, DateTimeOffset EndsAt);

    /// <summary>Who holds the remux job and until when, as the server reports it; null while no worker holds it.</summary>
    private static async Task<Lease?> LeaseAsync(Scenario scenario)
    {
        var job = (await scenario.JobsAsync(Scenario.RemuxKind)).FirstOrDefault(row => (string)row["status"]! == "leased");
        return job is null
            ? null
            : new Lease((string)job["lease_owner"]!, DateTimeOffset.Parse((string)job["lease_expires_at"]!, CultureInfo.InvariantCulture));
    }
}
