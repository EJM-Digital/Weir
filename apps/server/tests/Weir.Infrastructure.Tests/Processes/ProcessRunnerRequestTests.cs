using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Processes;

/// <summary>What <see cref="ProcessRunner"/> does with the request it is given: a request it refuses, the folder it runs in, and a priority it may not be allowed to set.</summary>
public sealed class ProcessRunnerRequestTests
{
    private static string[] Shell(string windows, string posix) =>
        OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c", windows] : ["/bin/sh", "-c", posix];

    [Fact]
    public async Task A_request_with_no_executable_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ProcessRunner().RunAsync(new ProcessRequest { Argv = [] }));
    }

    [Fact]
    public async Task A_request_without_a_request_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new ProcessRunner().RunAsync(null!));
    }

    [Fact]
    public async Task The_tool_runs_in_the_folder_the_request_names()
    {
        using var folder = new TempDirectory();

        var result = await new ProcessRunner().RunAsync(new ProcessRequest
        {
            Argv = Shell("cd", "pwd"),
            WorkingDirectory = folder.Path,
        });

        Assert.Equal(0, result.ExitCode);
        var printed = Encoding.UTF8.GetString(result.Stdout).Trim();
        Assert.Equal(Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(printed).TrimEnd(Path.DirectorySeparatorChar), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task A_priority_the_system_will_not_give_is_noted_and_the_tool_still_runs()
    {
        var logger = new RecordingLogger();

        var result = await new ProcessRunner(logger).RunAsync(new ProcessRequest
        {
            Argv = Shell("exit /b 0", "exit 0"),
            Priority = ProcessPriorityClass.High,
        });

        Assert.Equal(0, result.ExitCode);
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            Assert.Contains(logger.Messages, message => message.Contains("Could not set the priority", StringComparison.Ordinal));
        }
    }

    private sealed class RecordingLogger : ILogger<ProcessRunner>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
