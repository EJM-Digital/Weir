using Weir.Core.Auth;
using Weir.Core.Json;

namespace Weir.Core.Net;

/// <summary>
/// Skip sign-in for devices on the home network: whether it is on, the networks the owner added to the built-in local
/// ones, and the allow list that narrows which local addresses skip. Entries are CIDR ranges or bare addresses.
/// </summary>
public sealed record HomeNetworkSettings(bool Enabled, IReadOnlyList<string> ExtraNetworks, IReadOnlyList<string> AllowList)
{
    /// <summary>What every install has until its owner turns it on.</summary>
    public static HomeNetworkSettings Off { get; } = new(false, [], []);
}

/// <summary>
/// Which clients count as being on the home network. Only an address Weir can prove counts: the real socket peer, or the
/// client a trusted reverse proxy (<c>WEIR_TRUSTED_PROXY_IPS</c>) reports in <c>X-Forwarded-For</c>. Anything unclear is
/// unknown, and unknown never skips sign-in.
/// </summary>
public static class HomeNetwork
{
    private static readonly NetRange[] BuiltIn =
    [
        NetRange.ParseV4("127.0.0.0", 8),
        NetRange.ParseV4("10.0.0.0", 8),
        NetRange.ParseV4("172.16.0.0", 12),
        NetRange.ParseV4("192.168.0.0", 16),
        NetRange.ParseV4("169.254.0.0", 16),
        NetRange.ParseV6("::1", 128),
        NetRange.ParseV6("fc00::", 7),
        NetRange.ParseV6("fe80::", 10),
    ];

    /// <summary>True for a loopback, private, link-local or unique-local address, or one inside the owner's own networks.</summary>
    public static bool IsLocal(NetAddress address, IReadOnlyList<NetRange> extraNetworks)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(extraNetworks);
        var plain = Unmapped(address);
        return BuiltIn.Any(network => network.Contains(plain)) || extraNetworks.Any(network => network.Contains(plain));
    }

    /// <summary>
    /// True when <paramref name="client"/> is local and, when an allow list is set, is also on it. An unknown client, or
    /// a stored entry that cannot be read, never skips.
    /// </summary>
    public static bool Skips(NetAddress? client, HomeNetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled || client is null)
        {
            return false;
        }

        if (!IsLocal(client, Ranges(settings.ExtraNetworks)))
        {
            return false;
        }

        if (settings.AllowList.Count == 0)
        {
            return true;
        }

        var plain = Unmapped(client);
        return Ranges(settings.AllowList).Any(network => network.Contains(plain));
    }

    /// <summary>
    /// The client a request came from. The socket <paramref name="peer"/> counts unless it is a trusted proxy; then
    /// <paramref name="forwardedFor"/> is walked from the right, one trusted proxy at a time, to the first hop that is not
    /// one. A header the proxy left empty, a hop that is not an address, or a chain of nothing but proxies names no client.
    /// </summary>
    public static NetAddress? ClientOf(string? peer, string? forwardedFor, IReadOnlyList<NetRange> trustedProxies)
    {
        ArgumentNullException.ThrowIfNull(trustedProxies);
        if (!NetAddress.TryParse(peer, out var socket))
        {
            return null;
        }

        if (!ClientRateLimitKey.InNetworks(peer!, trustedProxies))
        {
            return socket;
        }

        var hops = (forwardedFor ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = hops.Length - 1; i >= 0; i--)
        {
            if (!NetAddress.TryParse(hops[i], out var hop))
            {
                return null;
            }

            if (!ClientRateLimitKey.InNetworks(hops[i], trustedProxies))
            {
                return hop;
            }
        }

        return null;
    }

    /// <summary>
    /// The entries trimmed, without blanks or repeats. Throws <see cref="WireValueException"/> naming every entry that is
    /// not an address or a range.
    /// </summary>
    public static IReadOnlyList<string> CleanEntries(IEnumerable<string>? entries)
    {
        var clean = (entries ?? [])
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalid = clean.Where(entry => !NetRange.TryParse(entry, strict: false, out _)).ToList();
        if (invalid.Count > 0)
        {
            throw new WireValueException(HomeNetworkWords.InvalidEntries(invalid));
        }

        return clean;
    }

    private static List<NetRange> Ranges(IEnumerable<string> entries) =>
        [.. entries.Select(entry => NetRange.TryParse(entry, strict: false, out var network) ? network : null).OfType<NetRange>()];

    private static NetAddress Unmapped(NetAddress address) => address.Ipv4Mapped ?? address;
}

/// <summary>What Weir says about skipping sign-in on the home network: the Activity line and the warning.</summary>
public static class HomeNetworkWords
{
    public const string ActivityTitle = "Local network sign-in setting changed";

    public const string AllowedHostsOff =
        "Allowed Hosts (above) is off. Weir currently answers on any address, so any device that can reach a local address here skips sign-in too. Turn on Allowed Hosts as well for this to only apply on your own network.";

    public static string ActivityDetail(bool enabled) =>
        $"Skip sign-in for devices on the local network turned {(enabled ? "on" : "off")}.";

    public static string InvalidEntries(IEnumerable<string> entries) =>
        $"'{string.Join("', '", entries)}' is not a valid address or subnet. Use an IP address (192.168.1.20) or a CIDR range (192.168.1.0/28).";

    /// <summary>The warning to show beside the setting, or <see langword="null"/> when there is nothing to warn about.</summary>
    public static string? Warning(HomeNetworkSettings settings, bool allowedHostsOn)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Enabled && !allowedHostsOn ? AllowedHostsOff : null;
    }
}
