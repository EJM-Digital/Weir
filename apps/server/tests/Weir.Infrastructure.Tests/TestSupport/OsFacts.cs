namespace Weir.Infrastructure.Tests;

/// <summary>A fact that runs only on Windows: share-mode, hard-link and mandatory-lock behaviour.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}

/// <summary>A fact that runs only off Windows: POSIX permission bits, symlinks and signal handling.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute(string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}

/// <summary>A theory that runs only on Windows.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}
