using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Host;

namespace Weir.Api.Tests.Startup;

/// <summary>The warnings written at start for a secret that is missing or too short; none of them stops the server.</summary>
public sealed class StartupMisconfigurationWarningTests
{
    private static List<string> Warnings(params (string Name, string Value)[] variables)
    {
        var home = Path.Join(Path.GetTempPath(), "weir-startup-warnings-" + Guid.NewGuid().ToString("N"));
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["WEIR_HOME"] = home };
        foreach (var (name, value) in variables)
        {
            environment[name] = value;
        }

        var options = WeirOptionsLoader.Load(new RuntimeEnvironment(environment, OperatingSystem.IsWindows(), home, home));
        var logger = new MessageLog();

        WeirServer.WarnStartupMisconfigurations(options, logger);

        return logger.Messages;
    }

    [Fact]
    public void A_credentials_secret_shorter_than_32_characters_is_called_out_with_its_length()
    {
        var warnings = Warnings(("WEIR_SESSION_SECRET", new string('s', 40)), ("WEIR_CREDENTIALS_SECRET", "too-short"));

        Assert.Equal(["WEIR_CREDENTIALS_SECRET is set but shorter than 32 characters (9 chars). Use a long random string to protect stored credentials."], warnings);
    }

    [Fact]
    public void A_long_credentials_secret_and_a_session_secret_need_no_warning()
    {
        Assert.Empty(Warnings(("WEIR_SESSION_SECRET", new string('s', 40)), ("WEIR_CREDENTIALS_SECRET", new string('c', 40))));
    }

    [Fact]
    public void A_missing_session_secret_is_called_out()
    {
        var warnings = Warnings(("WEIR_CREDENTIALS_SECRET", new string('c', 40)));

        Assert.Single(warnings);
        Assert.StartsWith("WEIR_SESSION_SECRET is not set", warnings[0], StringComparison.Ordinal);
    }

    private sealed class MessageLog : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
