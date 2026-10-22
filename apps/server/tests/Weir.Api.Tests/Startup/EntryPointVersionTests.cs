using Weir.Core;
using Weir.Host;

namespace Weir.Api.Tests.Startup;

/// <summary>The command line of the server program: asking for the version prints it and ends, without starting a server.</summary>
public sealed class EntryPointVersionTests
{
    [Fact]
    public void Asking_for_the_version_prints_it_and_ends_without_starting_a_server()
    {
        var entryPoint = typeof(WeirServer).Assembly.EntryPoint!;
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = (int)entryPoint.Invoke(null, [new[] { "--version" }])!;
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(0, exitCode);
        Assert.Contains(WeirVersion.Resolve(Environment.GetEnvironmentVariable("WEIR_VERSION")), captured.ToString(), StringComparison.Ordinal);
    }
}
