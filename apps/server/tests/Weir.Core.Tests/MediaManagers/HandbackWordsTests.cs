using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// What Weir tells a person about a media manager's word on a file it handed back, word for word, and how it tells two of
/// the manager's paths for one file apart from two files.
/// </summary>
public sealed class HandbackWordsTests
{
    [Fact]
    public void The_title_says_whether_a_manager_imported_the_file_refused_it_or_imported_it_after_all()
    {
        Assert.Equal("Deluno will not import Film.mkv", HandbackRules.OutcomeTitle("Deluno", HandbackRules.NotImported, "Film.mkv"));
        Assert.Equal("Deluno will not import Film.mkv", HandbackRules.OutcomeTitle("Deluno", HandbackRules.NotImported, "Film.mkv", afterAll: true));
        Assert.Equal("Deluno imported Film.mkv", HandbackRules.OutcomeTitle("Deluno", HandbackRules.Imported, "Film.mkv"));
        Assert.Equal("Deluno imported Film.mkv after all", HandbackRules.OutcomeTitle("Deluno", HandbackRules.Imported, "Film.mkv", afterAll: true));
    }

    [Fact]
    public void Each_note_says_what_happened_to_weirs_copy_in_plain_words()
    {
        Assert.Equal(
            "Deluno had said it would not import this file, and then imported it after all. Weir kept it.",
            HandbackRules.AfterAllMessage("Deluno", "Weir kept it."));
        Assert.Equal("Weir removed its copy from the hand-back folder, because Radarr has the file now.", HandbackRules.RemovedNote("Radarr"));
        Assert.Equal("Radarr moved Weir's copy into its library, so there was nothing for Weir to remove.", HandbackRules.AlreadyGoneNote("Radarr"));
        Assert.Equal(
            "Weir could not remove its copy (the file is open). It is still in the hand-back folder; remove it by hand if you want the space back.",
            HandbackRules.InUseNote("the file is open"));
        Assert.Equal(
            "Weir kept its copy, because Sonarr's messages to Weir carry no webhook secret, so Weir cannot be sure this one came from Sonarr. " +
            "Set a webhook secret for it in Setup › Connections › Media managers and Weir will tidy up after its imports.",
            HandbackRules.UnsignedNote("Sonarr"));
        Assert.Equal("Weir had already released its copy, so there was nothing for it to remove.", HandbackRules.StandingNote(released: true));
        Assert.Equal("Weir had already settled its copy, so it left it as it is.", HandbackRules.StandingNote(released: false));
        Assert.Equal("No media manager imported it within 1 day, so Weir removed its copy.", HandbackRules.UnclaimedNote(1));
        Assert.Equal("No media manager imported it within 14 days, so Weir removed its copy.", HandbackRules.UnclaimedNote(14));
        Assert.Equal("Weir's copy had already left the hand-back folder, so there was nothing to remove.", HandbackRules.UnclaimedGoneNote);
    }

    [Theory]
    [InlineData(null, "Deluno will not import this file. Weir kept its copy in the hand-back folder.")]
    [InlineData("", "Deluno will not import this file. Weir kept its copy in the hand-back folder.")]
    [InlineData("   ", "Deluno will not import this file. Weir kept its copy in the hand-back folder.")]
    [InlineData("It is a sample", "Deluno will not import this file: It is a sample. Weir kept its copy in the hand-back folder.")]
    [InlineData("  It is a sample.  ", "Deluno will not import this file: It is a sample. Weir kept its copy in the hand-back folder.")]
    public void A_refusal_names_the_managers_reason_when_it_gave_one(string? reason, string expected)
    {
        Assert.Equal(expected, HandbackRules.NotImportedNote("Deluno", reason));
    }

    [Fact]
    public void The_reply_to_an_outcome_is_built_from_what_that_outcome_did_to_the_copies_it_names()
    {
        const string kept = "Weir kept the copy.";

        Assert.Equal(
            "Weir recorded that the file will not be imported, and kept its copy.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.NotImported, 0, 0, 1, kept));
        Assert.Equal(
            "Weir recorded that the file will not be imported. Radarr has already imported it, so Weir left what it recorded about the file as it is.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.NotImported, 0, 0, 0, null, importedBy: "Radarr"));
        Assert.Equal(
            "Weir recorded that the file will not be imported, and kept its copy.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.NotImported, 0, 0, 1, kept, importedBy: "Radarr"));
        Assert.Equal(
            $"Weir recorded that Deluno imported the file. {HandbackRules.UnrecordedNote}",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.Imported, 0, 0, 0, null));
        Assert.Equal(
            $"Weir recorded that Deluno imported the file. {kept}",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.Imported, 1, 1, 1, kept));
        Assert.Equal(
            $"Weir recorded that Deluno imported the file. {HandbackRules.UnrecordedNote}",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.Imported, 0, 0, 1, null));
        Assert.Equal(
            "Weir recorded that Deluno imported the file and released its copy.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.Imported, 1, 0, 0, null));
        Assert.Equal(
            "Weir recorded that Deluno imported the file and released its copy.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.Imported, 2, 3, 0, null));
        Assert.Equal(
            $"Weir recorded that Deluno imported the file. {HandbackRules.AlreadyGoneNote("Deluno")}",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.Imported, 0, 1, 0, null));
        Assert.Equal(
            $"Weir recorded that Deluno imported the file. {HandbackRules.AlreadyGoneNote("Deluno")}",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.Imported, 0, 2, 0, null));
    }

    [Fact]
    public void A_manager_that_is_not_answering_is_named_in_each_waiting_message()
    {
        Assert.Equal(
            "Waiting for Sonarr, which is not answering. Weir will tell it this file is ready when it answers.",
            ManagerWaitMessages.ReportWaiting("Sonarr"));
        Assert.Equal("Weir gave up on telling Sonarr this file is ready, as you asked.", ManagerWaitMessages.ReportGivenUp("Sonarr"));
        Assert.Equal("Sonarr is answering again, and Weir has told it this file is ready.", ManagerWaitMessages.ReportDelivered("Sonarr"));
        Assert.Equal("Sonarr is not answering, so Weir could not reject this release.", ManagerWaitMessages.RejectNotAnswering("Sonarr"));
    }

    [Theory]
    [InlineData(@"C:\Films\A.mkv", @"c:\films\a.MKV", true)]
    [InlineData("C:/Films/A.mkv", "c:/films/a.mkv", true)]
    [InlineData(@"Films\A.mkv", @"films\a.mkv", true)]
    [InlineData("/Films/A.mkv", "/Films/A.mkv", true)]
    [InlineData("/Films/A.mkv", "/Films/B.mkv", false)]
    [InlineData("/Films/A.mkv", "/Films/Sub/A.mkv", false)]
    [InlineData("/Films/./A.mkv", "/Films/A.mkv", true)]
    [InlineData("/Films//A.mkv/", "/Films/A.mkv", true)]
    [InlineData("  /Films/A.mkv  ", "/Films/A.mkv", true)]
    [InlineData("", "/Films/A.mkv", false)]
    [InlineData("/Films/A.mkv", "", false)]
    [InlineData("   ", "/Films/A.mkv", false)]
    [InlineData("/Films/A.mkv", "   ", false)]
    [InlineData(null, "/Films/A.mkv", false)]
    [InlineData("/Films/A.mkv", null, false)]
    public void Two_paths_name_one_file_when_only_separators_dots_and_a_windows_paths_case_differ(string? left, string? right, bool same)
    {
        Assert.Equal(same, HandbackRules.SameManagerPath(left, right));
    }

    [Fact]
    public void A_path_that_is_not_windows_style_keeps_its_case_except_where_the_machine_ignores_it()
    {
        var caseless = OperatingSystem.IsWindows();

        Assert.Equal(caseless, HandbackRules.SameManagerPath("/Films/A.mkv", "/films/a.MKV"));
        Assert.Equal(caseless, HandbackRules.SameManagerPath("1:/Films/A.mkv", "1:/films/a.mkv"));
        Assert.Equal(caseless, HandbackRules.SameManagerPath("x", "X"));
    }

    [Theory]
    [InlineData("/Films/A.mkv", "A.mkv")]
    [InlineData(@"C:\Films\A.mkv", "A.mkv")]
    [InlineData("/Films/A.mkv/", "A.mkv")]
    [InlineData("A.mkv", "A.mkv")]
    [InlineData("/", "")]
    [InlineData("", "")]
    [InlineData("/./", "")]
    public void The_file_name_is_the_last_part_of_the_path_in_either_style(string path, string expected)
    {
        Assert.Equal(expected, HandbackRules.FileName(path));
    }
}
