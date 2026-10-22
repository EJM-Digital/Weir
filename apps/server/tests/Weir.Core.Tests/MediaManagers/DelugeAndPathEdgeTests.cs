using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>Deluge answers that are not shaped as hoped, and a path value that was never set.</summary>
public sealed class DelugeAndPathEdgeTests
{
    private static WireValue Parse(string json) => WireJsonParser.Parse(json);

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"result":["tv"],"error":{"message":"no label plugin"}}""")]
    [InlineData("""{"result":"tv"}""")]
    public void A_labels_answer_that_is_an_error_or_not_a_list_has_no_labels(string response) =>
        Assert.Empty(DelugeRules.LabelNames(Parse(response)));

    [Fact]
    public void Only_text_labels_are_names()
    {
        Assert.Equal(["tv", "movies"], DelugeRules.LabelNames(Parse("""{"result":["tv",3,"movies"],"error":null}""")));
        Assert.Empty(DelugeRules.LabelNames(null));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"result":{"apply_move_completed":true,"move_completed":true,"move_completed_path":"/done"},"error":{"message":"x"}}""")]
    [InlineData("""{"result":{"apply_move_completed":false,"move_completed":true,"move_completed_path":"/done"}}""")]
    [InlineData("""{"result":{"apply_move_completed":true,"move_completed":false,"move_completed_path":"/done"}}""")]
    [InlineData("""{"result":"x"}""")]
    public void A_label_whose_options_do_not_move_completed_downloads_has_no_folder(string options)
    {
        var folders = DelugeRules.Parse(null, [("tv", Parse(options))]);

        Assert.Empty(folders.CategoryFolders);
        Assert.Null(folders.CompletedFolder);
    }

    [Fact]
    public void A_label_that_moves_completed_downloads_has_its_own_folder()
    {
        var folders = DelugeRules.Parse(
            Parse("""{"result":{"move_completed":false,"download_location":"/downloads"}}"""),
            [("tv", Parse("""{"result":{"apply_move_completed":true,"move_completed":true,"move_completed_path":"/done/tv"}}"""))]);

        Assert.Equal("/downloads", folders.CompletedFolder);
        Assert.Equal("/done/tv", Assert.Single(folders.CategoryFolders).Folder);
    }

    [Fact]
    public void A_path_that_was_never_set_is_empty_and_of_no_kind()
    {
        var path = default(ArrOsPath);

        Assert.Equal(ArrOsPath.PathKind.Unknown, path.Kind);
        Assert.False(path.IsWindows);
        Assert.False(path.IsRooted);
        Assert.Empty(path.Segments);
        Assert.Equal(string.Empty, path.ToString());
    }

    [Theory]
    [InlineData(@"\\nas\share", true)]
    [InlineData(@"D:\media", true)]
    [InlineData(@"media\tv", false)]
    [InlineData("/media", true)]
    [InlineData("media/tv", false)]
    [InlineData("media", false)]
    public void A_path_is_rooted_by_its_own_style(string text, bool rooted) =>
        Assert.Equal(rooted, new ArrOsPath(text).IsRooted);
}
