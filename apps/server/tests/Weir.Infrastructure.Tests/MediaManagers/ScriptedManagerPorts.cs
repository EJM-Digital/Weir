using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// Ports whose answers a test writes, for the answers a real manager's reply never produces: a refusal that gives no words,
/// or a listing that says it has nothing to say. Any question the test did not script is a failure of the test.
/// </summary>
internal sealed class ScriptedManagerPorts : IMediaManagerPorts
{
    /// <summary>What <c>DescribeAsync</c> answers.</summary>
    public Func<ManagerConnection, ManagerDescription>? Describe { get; init; }

    /// <summary>What <c>ListLibraryFilesAsync</c> answers.</summary>
    public Func<ManagerConnection, string, ManagerLibraryFilesSignal>? ListFiles { get; init; }

    public IMediaManagerPort? PortForKind(string? kind) => new Port(kind!, this);

    /// <summary>A refusal to describe itself that says nothing about why.</summary>
    public static ManagerDescription Silent(ManagerConnection connection, string status = SignalStatus.Unreachable) =>
        new(connection, status, new ManagerCapabilities(new HashSet<string>(StringComparer.Ordinal), false, false, "none"), [], []);

    private sealed class Port(string kind, ScriptedManagerPorts owner) : IMediaManagerPort
    {
        public string Kind => kind;

        public ManagerCapabilities Capabilities() => new(new HashSet<string>(["movie", "tv"], StringComparer.Ordinal), false, false, "none");

        public Task<ManagerDescription> DescribeAsync(ManagerConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult((owner.Describe ?? throw new InvalidOperationException("The test scripted no description."))(connection));

        public Task<ManagerQueueSignal> QueueRowsAsync(ManagerConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveQueueItemAsync(ManagerConnection connection, WireObject row, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ManagerLibraryTruth> LibraryTruthAsync(ManagerConnection connection, string mediaScope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManagerLibraryFilesSignal> ListLibraryFilesAsync(ManagerConnection connection, string mediaScope, CancellationToken cancellationToken = default) =>
            Task.FromResult((owner.ListFiles ?? throw new InvalidOperationException("The test scripted no file listing."))(connection, mediaScope));

        public Task<ManagerNotifyOutcome> FileChangedAsync(
            ManagerConnection connection,
            IReadOnlySet<string> advertisedCapabilities,
            string? titleId,
            string filePath,
            string? reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
