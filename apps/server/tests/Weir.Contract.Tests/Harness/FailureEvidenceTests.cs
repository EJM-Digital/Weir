namespace Weir.Contract.Tests.Harness;

/// <summary>Checks that a server's logs are kept under the name of the test that used it, and removed again when the test passes.</summary>
[ContractArea("harness")]
public sealed class FailureEvidenceTests(ServerFixture shared) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private string? _nameWhenSetUp;

    public Task InitializeAsync()
    {
        _nameWhenSetUp = FailureEvidence.CurrentName;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void A_test_and_its_setup_run_under_the_name_of_the_test()
    {
        Assert.Equal($"{nameof(FailureEvidenceTests)}.{nameof(A_test_and_its_setup_run_under_the_name_of_the_test)}", FailureEvidence.CurrentName);
        Assert.Equal(FailureEvidence.CurrentName, _nameWhenSetUp);
    }

    [Fact]
    public void A_server_shared_by_the_class_is_kept_under_the_name_of_the_class()
    {
        Assert.Equal($"{nameof(FailureEvidenceTests)}.shared-server", shared.Server.EvidenceName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Each_row_of_a_theory_has_a_name_of_its_own(int row)
    {
        Assert.StartsWith($"{nameof(FailureEvidenceTests)}.{nameof(Each_row_of_a_theory_has_a_name_of_its_own)}-", FailureEvidence.CurrentName);
        Rows[row] = FailureEvidence.CurrentName!;
        Assert.Equal(Rows.Count, Rows.Values.Distinct().Count());
    }

    [Fact]
    public async Task A_server_stopped_by_its_test_leaves_its_logs_under_the_name_of_the_test()
    {
        var server = await WeirServer.StartNewAsync();
        await server.DisposeAsync();

        var folder = Path.Combine(FailureEvidence.FolderOf(FailureEvidence.CurrentName!), Path.GetFileName(server.Home));
        Assert.True(File.Exists(Path.Combine(folder, "logs", "weir.log")), "weir.log was not kept.");
        Assert.Contains("listening on", File.ReadAllText(Path.Combine(folder, "contract-logs", "server-1.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void Keeping_adds_the_error_and_discarding_removes_everything()
    {
        var name = $"{nameof(FailureEvidenceTests)}.{Guid.NewGuid():N}";
        var home = Directory.CreateTempSubdirectory("weir_contract_evidence_").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "logs"));
            File.WriteAllText(Path.Combine(home, "logs", "weir.log"), "a line");

            FailureEvidence.Archive(name, home);
            FailureEvidence.Keep(name, "it failed");

            var kept = FailureEvidence.FolderOf(name);
            Assert.Equal("it failed", File.ReadAllText(Path.Combine(kept, "error.txt")));
            Assert.Equal("a line", File.ReadAllText(Path.Combine(kept, Path.GetFileName(home), "logs", "weir.log")));

            FailureEvidence.Discard(name);

            Assert.False(Directory.Exists(kept));
        }
        finally
        {
            FailureEvidence.Discard(name);
            Directory.Delete(home, recursive: true);
        }
    }

    // Rows of a theory run one after another in one class instance each, so the names are gathered across instances.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Rows = new();
}
