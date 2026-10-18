using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="TransmissionRules"/>: RPC <c>session-get</c>'s <c>arguments.download-dir</c> (#768), read from what a real Transmission
/// answered (<c>fixtures/recorded/transmission</c>).
/// </summary>
public sealed class TransmissionRulesTests
{
    [Fact]
    public void The_only_folder_is_download_dir()
    {
        var reply = ArrRecording.Load("transmission", "folders").Exchanges[1].Reply;

        var folders = TransmissionRules.Parse(WireJsonParser.Parse(reply.Text));

        Assert.Equal("success", (string)reply.Body!["result"]!);
        Assert.Equal("/downloads/complete", folders.CompletedFolder);
        Assert.Empty(folders.CategoryFolders);
    }

    [Fact]
    public void No_arguments_yields_no_folder()
    {
        var folders = TransmissionRules.Parse(WireJsonParser.Parse("""{"result":"success"}"""));

        Assert.Null(folders.CompletedFolder);
    }
}
