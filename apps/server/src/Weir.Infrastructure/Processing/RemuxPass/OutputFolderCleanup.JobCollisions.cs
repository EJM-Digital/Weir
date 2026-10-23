using Weir.Core.Json;
using Weir.Core.Processing;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class OutputFolderCleanup
{
    private async Task<bool> MovieJobBlocksAsync(string relNorm, long? currentJobId, CancellationToken cancellationToken)
    {
        foreach (var job in await _data.ActiveRemuxJobsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (currentJobId is { } exclude && job.Id == exclude)
            {
                continue;
            }

            var scope = "movie";
            var jobRel = string.Empty;
            if (job.PayloadJson is { } raw && WireStrings.Strip(raw).Length > 0)
            {
                WireValue data;
                try
                {
                    data = WireJsonParser.Parse(raw);
                }
                catch (WireJsonDecodeException)
                {
                    continue;
                }

                if (data is WireObject dict)
                {
                    scope = ProcessingMediaScopes.Normalize((dict.Get("media_scope") as WireString)?.Value);
                    if (dict.Get("relative_media_path") is WireString jr && WireStrings.Strip(jr.Value).Length > 0)
                    {
                        jobRel = NormalizeRelativeForMatch(jr.Value);
                    }
                }
            }

            if (scope == "tv")
            {
                continue;
            }

            if (jobRel.Length > 0 && jobRel == relNorm)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TvJobBlocksAsync(string outputRoot, string seasonFolder, long? currentJobId, CancellationToken cancellationToken)
    {
        foreach (var job in await _data.ActiveRemuxJobsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (currentJobId is { } exclude && job.Id == exclude)
            {
                continue;
            }

            var scope = "movie";
            var jobRel = string.Empty;
            if (job.PayloadJson is { } raw && WireStrings.Strip(raw).Length > 0)
            {
                WireValue data;
                try
                {
                    data = WireJsonParser.Parse(raw);
                }
                catch (WireJsonDecodeException)
                {
                    continue;
                }

                if (data is WireObject dict)
                {
                    scope = dict.Get("media_scope") is WireString js && string.Equals(WireStrings.Strip(js.Value), "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
                    if (dict.Get("relative_media_path") is WireString jr && WireStrings.Strip(jr.Value).Length > 0)
                    {
                        jobRel = WireStrings.Strip(jr.Value);
                    }
                }
            }

            if (scope != "tv")
            {
                continue;
            }

            var relNorm = NormalizeRelativeForMatch(jobRel);
            if (relNorm.Length == 0)
            {
                continue;
            }

            var expected = RemuxPassPaths.Resolve(Path.Join(outputRoot, relNorm));
            if (!RemuxPassPaths.IsUnder(expected, outputRoot))
            {
                continue;
            }

            if (RemuxPassPaths.SamePath(Path.GetDirectoryName(expected)!, seasonFolder))
            {
                return true;
            }
        }

        return false;
    }
}
