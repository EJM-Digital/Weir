using System.Net;

namespace Weir.Tray;

/// <summary>The server process ended before it became ready.</summary>
sealed class ServerExitedException(int exitCode)
    : InvalidOperationException($"Weir server process exited unexpectedly with code {exitCode} before becoming healthy.")
{
    internal int ExitCode { get; } = exitCode;
}

/// <summary>The server process is alive but has stopped making progress: silent for longer than its phase allows.</summary>
sealed class ServerStartHungException(StartVerdict verdict, string lastAnswer)
    : TimeoutException(
        FormattableString.Invariant($"Weir did not become ready: it was silent for {verdict.SilentFor.TotalSeconds:0.#} s ") +
        FormattableString.Invariant($"(allowed {verdict.Allowed.TotalSeconds:0.#} s) {verdict.Where} (last answer to /ready: {lastAnswer})."))
{
    internal StartVerdict Verdict { get; } = verdict;
}

/// <summary>Waits for a starting server to answer its readiness endpoint, for as long as it keeps making progress.</summary>
static class ServerHealth
{
    /// <summary>How long one readiness request may take before it counts as "not ready yet".</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The pause between attempts, so a server that answers "not ready" quickly is not hammered.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Returns once <paramref name="readyUrl"/> answers 200 and the server process this start began has reported that its listener
    /// is up (<see cref="StartWatch.ReportedReady"/>, a record carrying its own process id). /ready is the readiness signal, as a
    /// Kubernetes readiness probe is, but it cannot say who answers: another Weir, or anything else, on the same port answers it too,
    /// while the server just started is about to fail to bind and exit. An answer is therefore not taken for the new server until it
    /// says it is listening, and a server that exits instead ends the wait as any exit does. Meanwhile the progress the server reports (<paramref name="watch"/>, fed from <paramref name="readProgress"/>) is the startup
    /// probe: a server is waited for for as long as it keeps making progress, and not for a fixed time. Throws
    /// <see cref="ServerExitedException"/> as soon as the server process has exited, and <see cref="ServerStartHungException"/> once it
    /// has been silent for longer than its phase allows; never a network error. <paramref name="exitCodeIfExited"/> returns the
    /// server's exit code once it has exited, and null while it runs.
    /// </summary>
    internal static async Task WaitUntilReadyAsync(
        HttpClient client,
        Uri readyUrl,
        Func<int?> exitCodeIfExited,
        StartWatch watch,
        Func<(StartProgress? FromPipe, StartProgress? FromFile)> readProgress,
        TimeSpan retryDelay,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (exitCodeIfExited() is { } exitCode)
            {
                throw new ServerExitedException(exitCode);
            }

            var answer = await AskAsync(client, readyUrl, cancellationToken).ConfigureAwait(false);
            var (fromPipe, fromFile) = readProgress();
            var verdict = watch.Check(fromPipe, fromFile);
            if (answer is null)
            {
                if (watch.ReportedReady)
                {
                    return;
                }

                answer = "HTTP 200, but this server has not reported that it is listening";
            }

            if (verdict.Hung)
            {
                throw new ServerStartHungException(verdict, answer);
            }

            await Task.Delay(retryDelay, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    // Null when the server is ready; otherwise what it (or the network) said instead.
    private static async Task<string?> AskAsync(HttpClient client, Uri readyUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client
                .GetAsync(readyUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (HttpRequestException ex)
        {
            // Nothing listening yet, or the connection dropped while the server started.
            return ex.Message;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"no answer within {client.Timeout.TotalSeconds:0.#}s";
        }
    }
}
