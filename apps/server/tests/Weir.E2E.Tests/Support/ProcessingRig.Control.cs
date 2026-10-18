using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>What a rig can do to the server itself, besides hand it files: hold its database, and add a person who may look but not change.</summary>
public sealed partial class ProcessingRig
{
    /// <summary>
    /// The write lock on the server's database, held as a backup tool would hold it: the server keeps answering, and every change it is asked
    /// for waits until the lock is let go, which is how a test looks at a button that says it is working.
    /// </summary>
    public HeldWriteLock HoldWrites() => HeldWriteLock.Take(_server);

    /// <summary>
    /// A viewer who can sign in with <see cref="SeededAccounts.ViewerUsername"/> and <see cref="SeededAccounts.ViewerPassword"/>. The server is
    /// stopped to add them and comes back on a new port, so <see cref="BaseUrl"/> and <see cref="Admin"/> are read again afterwards.
    /// </summary>
    public async Task AddViewerAsync()
    {
        await SeededAccounts.EnsureViewerAsync(_server);
        _admin.Dispose();
        _admin = new WeirClient(_server.BaseUrl);
        await _admin.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
    }
}
