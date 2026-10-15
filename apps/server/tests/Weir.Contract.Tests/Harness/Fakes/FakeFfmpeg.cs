using System.Reflection;
using System.Text.Json.Nodes;
using Weir.Contract.FakeTools;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>
/// A folder holding a fake <c>ffprobe</c> and a fake <c>ffmpeg</c>, for a server pointed at it with <see cref="Env"/>.
/// The tools are the Weir.Contract.FakeTools program copied twice; what each input "contains" and how each step ends comes from
/// the script this class writes beside them, and every call is logged for <see cref="Calls"/>. Dispose it after the server
/// using it has been stopped.
/// </summary>
public sealed class FakeFfmpeg : IDisposable
{
    private const string BuiltToolsFolderKey = "BuiltFakeToolsFolder";
    private const string ToolsProgram = "Weir.Contract.FakeTools";

    private static readonly TimeSpan CallsReadTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private JsonObject _script = new();
    private int _generation;

    private FakeFfmpeg(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>What the server needs to find the tools: pass it to <see cref="WeirServer.StartNewAsync"/>.</summary>
    public IReadOnlyDictionary<string, string> Env => new Dictionary<string, string> { ["WEIR_FFMPEG_DIR"] = Folder };

    /// <summary>Installs the tools into a new temporary folder with an empty script.</summary>
    public static FakeFfmpeg Install()
    {
        var folder = Directory.CreateTempSubdirectory("weir_contract_fake_ffmpeg_").FullName;
        try
        {
            var built = BuiltToolsFolder();
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var launcher = ToolsProgram + suffix;
            foreach (var file in Directory.EnumerateFiles(built).Where(path => Path.GetFileName(path) != launcher && Path.GetExtension(path) != ".xml"))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }

            // The launcher finds the program's assembly beside itself, so each copy runs the same program under its own name.
            foreach (var tool in new[] { "ffprobe", "ffmpeg" })
            {
                File.Copy(Path.Combine(built, launcher), Path.Combine(folder, tool + suffix));
            }

            var fake = new FakeFfmpeg(folder);
            fake.SetScript(new JsonObject());
            return fake;
        }
        catch
        {
            Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    /// <summary>Replaces the whole script: <c>{"files": {glob: rule}, "default": rule}</c>.</summary>
    public void SetScript(JsonObject script)
    {
        lock (_gate)
        {
            _script = (JsonObject)script.DeepClone();
            WriteScript();
        }
    }

    /// <summary>Sets what happens to files whose base name matches <paramref name="pattern"/>, replacing an earlier rule for the same pattern.</summary>
    public void SetFileRule(string pattern, FileRule? rule = null)
    {
        lock (_gate)
        {
            if (_script[FakeToolProtocol.FilesKey] is not JsonObject files)
            {
                files = new JsonObject();
                _script[FakeToolProtocol.FilesKey] = files;
            }

            files[pattern] = (rule ?? new FileRule()).ToJson();
            WriteScript();
        }
    }

    /// <summary>The calls made so far, oldest first, optionally only one tool and/or one step.</summary>
    public IReadOnlyList<ToolCall> Calls(string? tool = null, string? step = null)
    {
        var lines = ReadCallsFile();
        var calls = new List<ToolCall>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            JsonObject entry;
            try
            {
                entry = JsonNode.Parse(lines[i]) as JsonObject ?? throw new InvalidOperationException($"Not an object: {lines[i]}");
            }
            catch (System.Text.Json.JsonException) when (i == lines.Length - 1)
            {
                break; // a call being logged at this moment
            }

            var call = ToolCall.From(entry);
            if ((tool is null || call.Tool == tool) && (step is null || call.Step == step))
            {
                calls.Add(call);
            }
        }

        return calls;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            // A tool the server started may still hold a file; the temporary folder is cleaned up with the machine's.
        }
    }

    private static string BuiltToolsFolder() => typeof(FakeFfmpeg).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == BuiltToolsFolderKey)
        .Value ?? throw new InvalidOperationException("The contract project did not record where the fake tools are built.");

    private string[] ReadCallsFile()
    {
        var path = Path.Combine(Folder, FakeToolProtocol.CallsFile);
        var deadline = DateTime.UtcNow + CallsReadTimeout;
        while (true)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return [];
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n');
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10); // a tool holds the log for the moment it takes to append one line
            }
        }
    }

    // Each version goes to a file of its own, so there is no file to swap and nothing a running tool can hold up.
    private void WriteScript()
    {
        _generation++;
        File.WriteAllText(Path.Combine(Folder, FakeToolProtocol.ScriptFileName(_generation)), _script.ToJsonString());
    }
}
