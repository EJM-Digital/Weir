using System.Globalization;
using System.Numerics;

namespace Weir.Core.Configuration;

/// <summary>
/// The parsing rules the configuration loader applies to environment and config values: integer
/// parsing, URL host/port splitting and path expansion/normalization. They are fixed so values that
/// existing installs already set keep producing the same settings.
/// </summary>
internal static class ValueParsing
{
    /// <summary>
    /// Parses an already-trimmed integer: optional sign, ASCII digits, single underscores between
    /// digits (so 1_000 reads as 1000). Values beyond <see cref="long"/> saturate.
    /// </summary>
    public static bool TryParseInt(string raw, out long value)
    {
        value = 0;
        var span = raw.AsSpan();
        var negative = false;
        if (span.Length > 0 && (span[0] == '+' || span[0] == '-'))
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        if (span.Length == 0 || span[0] == '_' || span[^1] == '_')
        {
            return false;
        }

        var digits = new System.Text.StringBuilder(span.Length);
        var previousUnderscore = false;
        foreach (var c in span)
        {
            if (c == '_')
            {
                if (previousUnderscore)
                {
                    return false;
                }

                previousUnderscore = true;
                continue;
            }

            if (c is < '0' or > '9')
            {
                return false;
            }

            previousUnderscore = false;
            digits.Append(c);
        }

        var big = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            big = -big;
        }

        value = big > long.MaxValue ? long.MaxValue : big < long.MinValue ? long.MinValue : (long)big;
        return true;
    }

    /// <summary>The parts of a URL that the loopback-origin expansion reads.</summary>
    public sealed record ParsedUrl(string Scheme, string? Hostname, int? Port);

    /// <summary>
    /// Splits a URL into scheme, lower-cased hostname and port. A non-numeric or out-of-range port
    /// throws, which fails startup rather than silently dropping a configured origin.
    /// </summary>
    public static ParsedUrl ParseUrl(string raw)
    {
        var rest = raw;
        var scheme = string.Empty;
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && IsSchemeText(rest[..colon]))
        {
            scheme = rest[..colon].ToLowerInvariant();
            rest = rest[(colon + 1)..];
        }

        if (!rest.StartsWith("//", StringComparison.Ordinal))
        {
            return new ParsedUrl(scheme, null, null);
        }

        rest = rest[2..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var netloc = end < 0 ? rest : rest[..end];
        var hostinfo = netloc[(netloc.LastIndexOf('@') + 1)..];

        string hostname;
        string port;
        if (hostinfo.Contains('[', StringComparison.Ordinal))
        {
            var afterBracket = hostinfo[(hostinfo.IndexOf('[', StringComparison.Ordinal) + 1)..];
            var close = afterBracket.IndexOf(']', StringComparison.Ordinal);
            hostname = close < 0 ? afterBracket : afterBracket[..close];
            var tail = close < 0 ? string.Empty : afterBracket[(close + 1)..];
            port = tail.StartsWith(':') ? tail[1..] : string.Empty;
        }
        else
        {
            var portColon = hostinfo.IndexOf(':', StringComparison.Ordinal);
            hostname = portColon < 0 ? hostinfo : hostinfo[..portColon];
            port = portColon < 0 ? string.Empty : hostinfo[(portColon + 1)..];
        }

        int? parsedPort = null;
        if (port.Length > 0)
        {
            if (!port.All(char.IsAsciiDigit))
            {
                throw new WeirConfigurationException($"Port could not be cast to integer value as '{port}'");
            }

            var number = BigInteger.Parse(port, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number > 65535)
            {
                throw new WeirConfigurationException("Port out of range 0-65535");
            }

            parsedPort = (int)number;
        }

        return new ParsedUrl(scheme, hostname.Length == 0 ? null : hostname.ToLowerInvariant(), parsedPort);
    }

    private static bool IsSchemeText(string candidate) =>
        char.IsAsciiLetter(candidate[0]) &&
        candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');

    /// <summary>Expands the <c>~</c> and <c>~/…</c> forms to the user's home directory.</summary>
    public static string ExpandUser(string path, RuntimeEnvironment runtime)
    {
        if (path == "~")
        {
            return runtime.UserHomeDirectory;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            (runtime.IsWindows && path.StartsWith("~\\", StringComparison.Ordinal)))
        {
            return Path.Join(runtime.UserHomeDirectory, path[2..]);
        }

        return path;
    }

    /// <summary>Makes a path absolute against the working directory, normalized, with no trailing separator.</summary>
    public static string Resolve(string path, RuntimeEnvironment runtime) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, runtime.CurrentDirectory));

    /// <summary>
    /// <see cref="Resolve"/> for the value of <paramref name="variable"/>: a path the system cannot take (one with a NUL character, or too
    /// long) refuses the start with a message that names the variable, instead of failing with the system's own exception.
    /// </summary>
    public static string ResolveVariable(string variable, string path, RuntimeEnvironment runtime)
    {
        try
        {
            return Resolve(path, runtime);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new WeirConfigurationException($"{variable} is not a folder path Weir can use. Fix the value and start Weir again.");
        }
    }

    /// <summary>
    /// Lexical normalization without touching the file system: separators unified, duplicate
    /// separators and <c>.</c> segments dropped, no trailing separator; an empty path becomes <c>.</c>.
    /// </summary>
    public static string NormalizeLexically(string path, RuntimeEnvironment runtime)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        var separator = runtime.IsWindows ? '\\' : '/';
        var unified = runtime.IsWindows ? path.Replace('/', '\\') : path;
        var prefix = string.Empty;
        if (unified.StartsWith(new string(separator, 2), StringComparison.Ordinal) &&
            (unified.Length == 2 || unified[2] != separator))
        {
            prefix = new string(separator, 2);
        }
        else if (unified.StartsWith(separator))
        {
            prefix = separator.ToString();
        }

        var segments = unified[prefix.Length..]
            .Split(separator)
            .Where(segment => segment.Length > 0 && segment != ".");
        var joined = prefix + string.Join(separator, segments);
        return joined.Length == 0 ? "." : joined;
    }
}
