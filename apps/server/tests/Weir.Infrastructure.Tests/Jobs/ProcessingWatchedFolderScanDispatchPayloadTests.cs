using Weir.Core.Jobs;
using Weir.Core.Processing;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>What the scan dispatch handler does with a request it cannot act on: a payload that is not a request, a workflow with no watched folder, a scope with no workflow.</summary>
public sealed class ProcessingWatchedFolderScanDispatchPayloadTests
{
    private static async Task<Exception> HandleAsync(string? payloadJson, Func<Tests.Platform.StoreFixture, Task>? arrange = null)
    {
        var (store, _, handler) = await ProcessingWatchedFolderScanDispatchJobHandlerTests.BuildAsync();
        using var _ = store;
        if (arrange is not null)
        {
            await arrange(store);
        }

        return await Assert.ThrowsAnyAsync<Exception>(() =>
            handler.HandleAsync(new JobWorkContext(1, ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, payloadJson, "test-owner"), CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("7")]
    public async Task A_payload_that_is_not_a_json_object_is_refused(string payload)
    {
        var failure = await HandleAsync(payload);

        var refusal = Assert.IsType<ArgumentException>(failure);
        Assert.Contains("must be a JSON object", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Movies")]
    [InlineData("", "Movies")]
    [InlineData("   ", "Movies")]
    [InlineData("""{"media_scope":"movie"}""", "Movies")]
    [InlineData("""{"media_scope":"tv"}""", "TV")]
    public async Task A_workflow_with_no_watched_folder_cannot_be_scanned(string? payload, string label)
    {
        var failure = await HandleAsync(payload);

        var refusal = Assert.IsType<InvalidOperationException>(failure);
        Assert.StartsWith($"The {label} workflow has no watched folder", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"media_scope":"movie"}""", "Movies")]
    [InlineData("""{"media_scope":"tv"}""", "TV")]
    public async Task A_scope_no_workflow_covers_says_which_one(string payload, string label)
    {
        var failure = await HandleAsync(payload, store => store.Execute("DELETE FROM libraries"));

        var refusal = Assert.IsType<InvalidOperationException>(failure);
        Assert.StartsWith($"No workflow covers {label}.", refusal.Message, StringComparison.Ordinal);
    }
}
