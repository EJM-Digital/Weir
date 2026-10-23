using Weir.Core.Processing;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>When a file's own row leaves it to the hand-back record to say whether the source was cleaned before.</summary>
public sealed class CleanedSourcesRowTests
{
    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    [InlineData(ProcessingFileStatuses.Processed, true, true)]
    [InlineData(ProcessingFileStatuses.Unprocessed, false, true)]
    [InlineData(ProcessingFileStatuses.Unprocessed, true, false)]
    [InlineData(ProcessingFileStatuses.ProcessingFailed, false, false)]
    [InlineData(ProcessingFileStatuses.OnHold, false, false)]
    public void A_file_the_list_lost_one_that_is_done_or_one_never_worked_on_is_left_to_the_record_and_any_other_is_not(string? status, bool workedOn, bool leaves)
    {
        Assert.Equal(leaves, CleanedSources.RowLeavesItToTheRecord(status, workedOn));
    }
}
