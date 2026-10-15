using Weir.Contract.FakeTools;

var folder = ToolFolder.Locate();
var script = new ToolScript(folder.LoadScript());
var tool = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty).ToUpperInvariant();
try
{
    return tool.StartsWith("FFPROBE", StringComparison.Ordinal)
        ? FakeFfprobe.Run(folder, script, args)
        : FakeFfmpegTool.Run(folder, script, args);
}
catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or TimeoutException)
{
    Output.Error(problem.Message);
    return 1;
}
