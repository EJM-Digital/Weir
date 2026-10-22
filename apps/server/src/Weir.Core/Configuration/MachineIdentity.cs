namespace Weir.Core.Configuration;

/// <summary>
/// The name this Weir goes by: the host name of the machine it runs on (#826). It is read, never set, so the
/// name people see in the browser, in alerts and in a media manager always matches the computer.
/// </summary>
public sealed record MachineIdentity
{
    /// <summary>What a container is called when its compose file gives it no <c>hostname:</c>: twelve hex characters.</summary>
    private const int GeneratedContainerNameLength = 12;

    private const string UnknownMachineName = "this computer";

    private MachineIdentity(string name)
    {
        Name = name;
    }

    /// <summary>The host name as the operating system reports it.</summary>
    public string Name { get; }

    /// <summary>How the app introduces itself: "Weir on RIG".</summary>
    public string AppName => $"Weir on {Name}";

    /// <summary>
    /// Whether the host name looks generated rather than chosen, which is what a container gets when its compose
    /// file does not set <c>hostname:</c>.
    /// </summary>
    public bool LooksGenerated => Name.Length == GeneratedContainerNameLength && Name.All(char.IsAsciiHexDigitLower);

    /// <summary>This machine's identity.</summary>
    public static MachineIdentity Current() => Current(() => Environment.MachineName);

    /// <summary>This machine's identity from <paramref name="machineName"/>; a system that cannot give a name reads as "this computer".</summary>
    internal static MachineIdentity Current(Func<string> machineName)
    {
        try
        {
            return From(machineName());
        }
        catch (InvalidOperationException)
        {
            return From(null);
        }
    }

    /// <summary>An identity for a given host name; a blank one reads as "this computer".</summary>
    public static MachineIdentity From(string? hostName)
    {
        var name = (hostName ?? string.Empty).Trim();
        return new MachineIdentity(name.Length == 0 ? UnknownMachineName : name);
    }
}
