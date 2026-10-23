using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>The media manager services refuse to be built without the parts they work through.</summary>
public sealed class ConstructionGuardTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void The_intake_cannot_be_built_without_any_of_its_parts() =>
        ConstructorGuards.AssertEachRequiredPartIsRefused<MediaManagerIntake>(() =>
        [
            _fixture.Store.Options, _fixture.Connections, _fixture.ConnectionStore, _fixture.Ledger, _fixture.Targets, _fixture.Jobs,
            _fixture.SkipMarkers, _fixture.Reporter, _fixture.Artwork, _fixture.Store.Clock, _fixture.Activity,
        ]);

    [Fact]
    public void The_workflow_sync_cannot_be_built_without_any_of_its_parts() =>
        ConstructorGuards.AssertEachRequiredPartIsRefused<ManagerWorkflowSync>(() =>
        [
            _fixture.Store.Database, _fixture.ConnectionStore, _fixture.Connections, _fixture.Http, _fixture.Libraries, new ScanSettingsChanges(),
            _fixture.Store.Options, _fixture.Store.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagerWorkflowSync>.Instance,
        ]);

    [Fact]
    public void The_reporter_cannot_be_built_without_any_of_its_parts() =>
        ConstructorGuards.AssertEachRequiredPartIsRefused<HandoffCompletionReporter>(() =>
        [
            _fixture.Connections, _fixture.ConnectionStore, _fixture.Ledger, _fixture.Targets, _fixture.Libraries, _fixture.Http, _fixture.Store.Clock, null,
        ]);
}
