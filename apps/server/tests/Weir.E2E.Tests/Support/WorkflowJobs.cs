using System.Net;
using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>Waits on a workflow's queued and running jobs, which decide whether Weir will remove it; Weir looks at a new workflow's folders at once, which queues a job for a moment.</summary>
public static class WorkflowJobs
{
    /// <summary>Waits until the workflow has work queued or running, which is what makes Weir refuse to remove it.</summary>
    public static Task WaitUntilBusyAsync(WeirClient admin, long id) => WaitForAsync(admin, id, busy: true);

    /// <summary>Waits until the workflow has no work queued or running.</summary>
    public static Task WaitUntilIdleAsync(WeirClient admin, long id) => WaitForAsync(admin, id, busy: false);

    private static Task WaitForAsync(WeirClient admin, long id, bool busy) =>
        Poll.UntilAsync(
            async () =>
            {
                var read = await admin.GetAsync($"{WeirClient.Api}/processing/libraries/{id}");
                Assert.True(read.Status == HttpStatusCode.OK, read.ToString());
                return (int)read.Fields["active_job_count"]! > 0 == busy;
            },
            $"workflow {id} to have {(busy ? "work queued" : "no work queued")}");
}
