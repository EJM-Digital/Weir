namespace Weir.Tray;

/// <summary>The exit codes the server uses to say why a start ended, where the tray words them differently from any other exit.</summary>
static class ServerExitCodes
{
    /// <summary>
    /// The server could not listen because another program holds its port: <c>EX_TEMPFAIL</c> in BSD's sysexits, "a temporary failure, the
    /// user is invited to retry" (<c>PortInUseExitCode</c> in the server's <c>StartRefusal</c>; keep the two in step). The other program can
    /// let go, so the tray tries again as it does for any failed start, and names the port when it stops trying.
    /// </summary>
    internal const int PortInUse = 75;
}
