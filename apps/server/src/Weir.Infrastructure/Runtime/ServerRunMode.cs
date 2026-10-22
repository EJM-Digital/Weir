namespace Weir.Infrastructure.Runtime;

/// <summary>How this copy of Weir was started: by the system as a service, by a person as an app, or inside a container.</summary>
public sealed record ServerRunMode
{
    public static readonly ServerRunMode Service = new("service");

    public static readonly ServerRunMode App = new("app");

    public static readonly ServerRunMode Docker = new("docker");

    private ServerRunMode(string name) => Name = name;

    /// <summary>The word the API uses: <c>service</c>, <c>app</c> or <c>docker</c>.</summary>
    public string Name { get; }

    /// <summary>A container is Docker whatever started it; otherwise either service manager (the Windows one or systemd) makes it a service.</summary>
    public static ServerRunMode Detect(string installType, bool startedByWindowsService, bool startedBySystemd)
    {
        ArgumentNullException.ThrowIfNull(installType);
        return installType == "docker" ? Docker : startedByWindowsService || startedBySystemd ? Service : App;
    }
}
