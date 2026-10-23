using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Tests.Media;

namespace Weir.Infrastructure.Tests.Auth;

/// <summary>The setup code a new install publishes at start-up: an instruction to follow, not a problem to look into.</summary>
public sealed class SetupCodeGateTests : IDisposable
{
    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    private SetupCodeGate Gate() => new(WeirOptionsLoader.Load(new RuntimeEnvironment(
        new Dictionary<string, string>(StringComparer.Ordinal) { ["WEIR_HOME"] = _home.Path },
        OperatingSystem.IsWindows(),
        _home.Path,
        _home.Path)));

    [Fact]
    public void An_install_with_no_account_says_where_to_find_the_setup_code_as_information_not_a_warning()
    {
        var logger = new ListLogger<SetupCodeGate>();
        var codeFile = _home.Join("setup-code");

        Gate().EnsureStateForStartup(adminExists: false, logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        var code = File.ReadAllText(codeFile).Trim();
        Assert.Equal(
            $"Weir has no account yet. To create one from another device, enter this setup code: {code}. It is also in {codeFile}.",
            entry.Message);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not an address", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    public void Only_a_loopback_address_is_a_loopback_caller(string? host, bool loopback) =>
        Assert.Equal(loopback, SetupCodeGate.IsLoopback(host));

    [Fact]
    public void A_code_file_that_cannot_be_deleted_is_left_behind_without_failing_start_up()
    {
        var gate = Gate();
        Directory.CreateDirectory(_home.Join("setup-code"));

        gate.Clear();

        Assert.True(Directory.Exists(_home.Join("setup-code")));
    }

    [Fact]
    public void A_data_folder_that_is_really_a_file_has_no_code_file_to_delete()
    {
        var notAFolder = _home.Join("not-a-folder");
        File.WriteAllText(notAFolder, "x");
        var gate = new SetupCodeGate(WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WEIR_HOME"] = notAFolder },
            OperatingSystem.IsWindows(),
            _home.Path,
            _home.Path)));

        gate.Clear();

        Assert.False(gate.HasCode);
    }

    [Fact]
    public void An_install_with_an_account_says_nothing_and_leaves_no_code()
    {
        var logger = new ListLogger<SetupCodeGate>();
        var gate = Gate();
        gate.EnsureStateForStartup(adminExists: false, new ListLogger<SetupCodeGate>());

        gate.EnsureStateForStartup(adminExists: true, logger);

        Assert.Empty(logger.Entries);
        Assert.False(gate.HasCode);
        Assert.False(File.Exists(_home.Join("setup-code")));
    }
}
