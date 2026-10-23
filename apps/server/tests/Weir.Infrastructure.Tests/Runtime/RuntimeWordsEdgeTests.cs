using Microsoft.Data.Sqlite;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The names and sentences the server writes for values it does not expect, and for failures with no cause a person can act on.</summary>
public sealed class RuntimeWordsEdgeTests
{
    private const string Unexpected = "Something unexpected went wrong. The details are in Weir's log files.";

    [Theory]
    [InlineData(StartupPhase.Starting, "starting")]
    [InlineData(StartupPhase.OpeningDatabase, "opening_database")]
    [InlineData(StartupPhase.SavingCopy, "saving_copy")]
    [InlineData(StartupPhase.Migrating, "migrating")]
    [InlineData(StartupPhase.Preparing, "preparing")]
    [InlineData(StartupPhase.StartingListener, "starting_listener")]
    [InlineData(StartupPhase.Ready, "ready")]
    public void Every_start_up_phase_has_the_name_the_tray_reads(StartupPhase phase, string name)
    {
        Assert.Equal(name, StartupProgress.WireName(phase));
    }

    [Fact]
    public void A_start_up_phase_that_does_not_exist_has_no_name()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StartupProgress.WireName((StartupPhase)999));
    }

    [Theory]
    [InlineData(LocalFolder.Watched, "watched")]
    [InlineData(LocalFolder.Work, "work")]
    [InlineData(LocalFolder.Output, "output")]
    public void Every_workflow_folder_has_the_role_name_the_health_screen_uses(LocalFolder folder, string role)
    {
        Assert.Equal(role, FolderReachability.Role(folder));
    }

    [Fact]
    public void A_workflow_folder_that_does_not_exist_has_no_role()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FolderReachability.Role((LocalFolder)999));
    }

    [Fact]
    public void A_database_error_with_no_cause_a_person_can_act_on_is_worded_as_unexpected()
    {
        var failure = new SqliteException("something else", 1);

        Assert.Equal(Unexpected, PreUpdateBackupFailure.Describe(failure, "/data/backups"));
    }

    [Fact]
    public void A_disk_error_with_no_cause_a_person_can_act_on_is_worded_as_unexpected()
    {
        Assert.Equal(Unexpected, PreUpdateBackupFailure.Describe(new IOException("odd", 12345), "/data/backups"));
    }

    [WindowsFact("Only Windows has network shares named by a server and a share.")]
    public void A_full_network_share_is_named_as_the_share()
    {
        var words = PreUpdateBackupFailure.Describe(new IOException("full", 28), @"\\nas\weir\backups");

        Assert.Equal(@"The share \\nas\weir is full. Free up some space there, then try again.", words);
    }
}
