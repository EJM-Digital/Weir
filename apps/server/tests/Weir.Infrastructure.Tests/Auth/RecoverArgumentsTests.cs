using Weir.Infrastructure.Auth;

namespace Weir.Infrastructure.Tests.Auth;

/// <summary>The command line of <c>Weir recover</c>: each option as <c>--name value</c> and as <c>--name=value</c>, and what is said when a value is missing.</summary>
public sealed class RecoverArgumentsTests
{
    [Fact]
    public void An_option_may_carry_its_value_after_an_equals_sign()
    {
        var parsed = RecoverArguments.Parse(["--username=alice", "--password=s3cret=x"]);

        Assert.Null(parsed.Error);
        Assert.Equal("alice", parsed.Username);
        Assert.Equal("s3cret=x", parsed.Password);
    }

    [Fact]
    public void An_option_may_carry_its_value_as_the_next_argument()
    {
        var parsed = RecoverArguments.Parse(["--username", "alice", "--password", "s3cret", "--list"]);

        Assert.Equal(("alice", "s3cret", true), (parsed.Username, parsed.Password, parsed.List));
    }

    [Theory]
    [InlineData("--username")]
    [InlineData("--password")]
    public void An_option_at_the_end_with_no_value_is_refused(string option)
    {
        var parsed = RecoverArguments.Parse([option]);

        Assert.Equal($"argument {option}: expected one argument", parsed.Error);
    }

    [Fact]
    public void Help_and_stray_arguments_are_told_apart()
    {
        Assert.True(RecoverArguments.Parse(["--help"]).ShowHelp);
        Assert.Equal("unrecognized arguments: one two", RecoverArguments.Parse(["one", "two"]).Error);
    }
}
