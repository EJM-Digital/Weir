using System.Text;

namespace Weir.Infrastructure.Auth;

/// <summary>
/// Reads the new password from the operator when <c>--password</c> was not given.
/// </summary>
public interface IRecoverPasswordPrompt
{
    /// <summary>Whether a password can be prompted for at all (standard input is an interactive console).</summary>
    bool IsInteractive { get; }

    /// <summary>Read one line from the operator without echoing it.</summary>
    string ReadPassword(string prompt);
}

/// <summary>
/// The real console: no echo at all, not even the masking characters some prompts show, so the
/// password's length is not revealed either.
/// </summary>
public sealed class ConsoleRecoverPasswordPrompt : IRecoverPasswordPrompt
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public string ReadPassword(string prompt) => ReadPassword(prompt, Console.ReadKey, Console.Out);

    /// <summary>Test seam (<c>InternalsVisibleTo</c> to Weir.Infrastructure.Tests): the same prompt over keys and a writer a test supplies, since a test run has no console to type at.</summary>
    internal static string ReadPassword(string prompt, Func<bool, ConsoleKeyInfo> readKey, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        output.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = readKey(true);
            if (key.Key == ConsoleKey.Enter)
            {
                output.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
