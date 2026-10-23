using Weir.Infrastructure.Auth;

namespace Weir.Infrastructure.Tests.Auth;

/// <summary>The password is read key by key without echo; the keys here stand in for the ones a person types.</summary>
public sealed class ConsoleRecoverPasswordPromptTests
{
    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.A, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Special(ConsoleKey key, char c = '\0') => new(c, key, shift: false, alt: false, control: false);

    private static Func<bool, ConsoleKeyInfo> Typing(params ConsoleKeyInfo[] keys)
    {
        var queue = new Queue<ConsoleKeyInfo>(keys);
        return intercept =>
        {
            Assert.True(intercept, "A password is never echoed.");
            return queue.Dequeue();
        };
    }

    [Fact]
    public void The_typed_characters_are_returned_and_nothing_but_the_prompt_and_a_line_break_is_written()
    {
        var output = new StringWriter();

        var password = ConsoleRecoverPasswordPrompt.ReadPassword("New password: ", Typing(Key('a'), Key('b'), Special(ConsoleKey.Enter, '\r')), output);

        Assert.Equal("ab", password);
        Assert.Equal("New password: " + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Backspace_removes_the_last_character_and_does_nothing_when_there_is_none()
    {
        var keys = Typing(
            Special(ConsoleKey.Backspace, '\b'),
            Key('a'),
            Key('b'),
            Special(ConsoleKey.Backspace, '\b'),
            Key('c'),
            Special(ConsoleKey.Enter, '\r'));

        Assert.Equal("ac", ConsoleRecoverPasswordPrompt.ReadPassword("> ", keys, new StringWriter()));
    }

    [Fact]
    public void Control_keys_that_are_not_characters_are_ignored()
    {
        var keys = Typing(Key('a'), Special(ConsoleKey.Tab, '\t'), Special(ConsoleKey.LeftArrow), Key('b'), Special(ConsoleKey.Enter, '\r'));

        Assert.Equal("ab", ConsoleRecoverPasswordPrompt.ReadPassword("> ", keys, new StringWriter()));
    }

    [Fact]
    public void A_missing_prompt_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ConsoleRecoverPasswordPrompt.ReadPassword(null!, Typing(), new StringWriter()));
    }

    [Fact]
    public void The_console_prompt_is_interactive_only_when_input_is_not_redirected()
    {
        Assert.Equal(!Console.IsInputRedirected, new ConsoleRecoverPasswordPrompt().IsInteractive);
    }

    [Fact]
    public void Reading_from_a_redirected_console_is_refused_by_the_console_itself()
    {
        // With a person at the keyboard the read would wait for them, so the check only runs where input is piped (the test hosts).
        if (!Console.IsInputRedirected)
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() => new ConsoleRecoverPasswordPrompt().ReadPassword(string.Empty));
    }
}
