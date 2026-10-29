using Weir.Core.Configuration;

namespace Weir.Core.Net;

/// <summary>
/// Which Host header values Weir answers. Guards against DNS rebinding, since a server bound to every
/// interface otherwise answers whatever Host a client sends.
/// </summary>
public static class AllowedHostPolicy
{
    /// <summary>Weir's host check has no off switch: <c>WEIR_ALLOWED_HOSTS</c> only adds names to what it answers.</summary>
    public const bool Enforced = true;

    /// <summary>
    /// Suffixes for names a home or small-office network typically resolves only for itself, so a
    /// public DNS rebind cannot produce them.
    /// </summary>
    private static readonly string[] LocalNetworkSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain"];

    /// <summary>
    /// <see langword="true"/> when <paramref name="hostHeader"/> is one Weir should answer:
    /// an IP literal, <c>localhost</c>, a single-label name, a <see cref="LocalNetworkSuffixes"/> name,
    /// a host from <paramref name="trustedBrowserOrigins"/>, or an entry in <paramref name="allowedHosts"/>
    /// (a leading <c>*.</c> entry also allows its subdomains).
    /// </summary>
    public static bool IsAllowed(string hostHeader, IReadOnlyList<string> allowedHosts, IReadOnlyList<string> trustedBrowserOrigins)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        ArgumentNullException.ThrowIfNull(trustedBrowserOrigins);
        var host = WithoutPort(hostHeader);
        if (host.Length == 0)
        {
            return false;
        }

        if (IsIpLiteral(host) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            !host.Contains('.', StringComparison.Ordinal) ||
            Array.Exists(LocalNetworkSuffixes, suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return MatchesConfiguredEntry(host, allowedHosts) || MatchesOriginHost(host, trustedBrowserOrigins);
    }

    /// <summary>The Host header's address, dropping a trailing <c>:port</c> but keeping IPv6 brackets.</summary>
    private static string WithoutPort(string hostHeader)
    {
        var value = (hostHeader ?? string.Empty).Trim();
        if (value.Length == 0 || value[0] == '[')
        {
            var close = value.IndexOf(']');
            return close < 0 ? value : value[..(close + 1)];
        }

        // A bare (unbracketed) IPv6 literal has no port in the Host header, so only a single colon
        // is ever a host:port separator; two or more colons with no brackets is the address itself.
        var firstColon = value.IndexOf(':');
        var lastColon = value.LastIndexOf(':');
        return firstColon >= 0 && firstColon == lastColon ? value[..firstColon] : value;
    }

    private static bool IsIpLiteral(string host)
    {
        var candidate = host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        return NetAddress.TryParse(candidate, out _);
    }

    private static bool MatchesConfiguredEntry(string host, IReadOnlyList<string> entries)
    {
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = entry[1..]; // the entry minus its leading '*', so the dot stays
                if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(host, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesOriginHost(string host, IReadOnlyList<string> origins)
    {
        foreach (var origin in origins)
        {
            if (ValueParsing.ParseUrl(origin.Trim()).Hostname is { } originHost && string.Equals(host, originHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
