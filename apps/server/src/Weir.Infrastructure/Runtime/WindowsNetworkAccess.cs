using Weir.Core.Configuration;

namespace Weir.Infrastructure.Runtime;

/// <summary>Reads what Windows Firewall says about Weir's server program.</summary>
public interface IServerFirewall
{
    FirewallVerdict Judge();
}

/// <summary>The addresses other devices on the network would type to reach this PC.</summary>
public interface ILanAddresses
{
    IReadOnlyList<string> Read();
}

/// <summary>
/// The Windows package's network access. The choice is a file the tray watches (<see cref="LanAccessFile"/>); the
/// tray restarts the server for it and, when Weir's firewall rule is missing, asks for it through Windows' own
/// administrator prompt on this PC. Until the restart the server still listens the old way, which is what makes a
/// saved choice that differs from <see cref="ServerListenOptions"/> a pending one.
/// </summary>
public sealed class WindowsNetworkAccess : INetworkAccess
{
    private readonly ServerListenOptions _listen;
    private readonly LanAccessFile _choice;
    private readonly IServerFirewall _firewall;
    private readonly ILanAddresses _addresses;

    public WindowsNetworkAccess(ServerListenOptions listen, LanAccessFile choice, IServerFirewall firewall, ILanAddresses addresses)
    {
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(firewall);
        ArgumentNullException.ThrowIfNull(addresses);
        _listen = listen;
        _choice = choice;
        _firewall = firewall;
        _addresses = addresses;
    }

    public string? NotChangeableReason => null;

    public NetworkAccessStatus Read()
    {
        var scope = _listen.IsThisPcOnly ? NetworkScope.ThisPcOnly : NetworkScope.Network;
        var pending = _choice.Read() is { } saved && saved != scope ? saved : (NetworkScope?)null;
        var wantsNetwork = scope == NetworkScope.Network || pending == NetworkScope.Network;

        var firewall = wantsNetwork ? _firewall.Judge() : FirewallVerdict.NotChecked;
        var addresses = wantsNetwork
            ? _addresses.Read().Select(address => $"http://{address}:{_listen.Port}").ToList()
            : [];
        return new NetworkAccessStatus(StateOf(scope, firewall), scope, pending, firewall, _listen.Port, addresses);
    }

    public void Choose(NetworkScope scope) => _choice.Write(scope);

    private static NetworkAccessState StateOf(NetworkScope scope, FirewallVerdict firewall) => scope switch
    {
        NetworkScope.ThisPcOnly => NetworkAccessState.ThisPcOnly,
        _ when firewall == FirewallVerdict.Allows => NetworkAccessState.Allowed,
        _ => NetworkAccessState.Blocked,
    };
}
