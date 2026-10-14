using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// A record of every server this test run started, so a run that was killed before its teardown cannot leave
/// servers behind. One file per server, written by the run that owns it; a later run stops only servers whose
/// owner has gone, so runs side by side never touch each other's servers.
/// </summary>
/// <remarks>
/// The folder is shared by every fixture of a run and by every run on the machine, so its callers meet: each
/// fixture looks for orphans as it starts a server while another is forgetting its own. A file that is read
/// or deleted by someone else at that moment is theirs, and the call carries on without it. An orphan is
/// claimed by making a claim file beside its entry, which only one caller can make, and only the claimer stops the server.
/// </remarks>
public sealed class ServerLedger
{
    private static readonly Lazy<ServerLedger> SharedLedger = new(() => new ServerLedger(DefaultFolder()));

    private readonly string _folder;

    public ServerLedger(string folder) => _folder = folder;

    public static ServerLedger Shared => SharedLedger.Value;

    private sealed record Entry(int ServerProcessId, long ServerStartTicks, int OwnerProcessId, long OwnerStartTicks);

    public void Record(Process server)
    {
        Directory.CreateDirectory(_folder);
        using var owner = Process.GetCurrentProcess();
        var entry = new Entry(server.Id, StartTicks(server), owner.Id, StartTicks(owner));
        File.WriteAllText(EntryPath(server.Id), JsonSerializer.Serialize(entry));
    }

    /// <summary>
    /// Takes a stopped server out of the ledger. A file someone is reading at this moment is left for the next run to find,
    /// which sees the owner gone and the server not there, and clears it.
    /// </summary>
    public void Forget(int serverProcessId) => TryDelete(EntryPath(serverProcessId));

    /// <summary>Stops every recorded server whose test run is gone, and returns how many this call stopped.</summary>
    public int StopOrphans()
    {
        var stopped = 0;
        foreach (var file in Entries())
        {
            var entry = Read(file);
            if (entry is null || IsRunning(entry.OwnerProcessId, entry.OwnerStartTicks))
            {
                continue;
            }

            // The entry is checked again under the claim: a caller that held it before us has stopped the server and
            // removed the entry before it let go.
            using var claim = TryClaim(file);
            if (claim is null || !File.Exists(file))
            {
                continue;
            }

            try
            {
                stopped += StopIfStillTheSameProcess(entry) ? 1 : 0;
            }
            finally
            {
                TryDelete(file);
            }
        }

        return stopped;
    }

    private List<string> Entries()
    {
        try
        {
            return Directory.EnumerateFiles(_folder, "*.json").ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static bool StopIfStillTheSameProcess(Entry entry)
    {
        // The start time guards against a process id the operating system has since given to something else.
        try
        {
            using var process = Process.GetProcessById(entry.ServerProcessId);
            if (StartTicks(process) != entry.ServerStartTicks)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static bool IsRunning(int processId, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && StartTicks(process) == startTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // A process this one may not look at is not one it may stop either: its owner counts as running.
            return true;
        }
    }

    private static Entry? Read(string file)
    {
        try
        {
            // Shared with a deleter and a mover, so that reading a file never makes someone else's delete fail.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Entry>(stream);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes the right to stop an entry's server: a file made beside it that only one caller can make. It lives as long as the
    /// returned stream is open, and the system removes it if its holder dies, so a claim never outlasts the caller that made it.
    /// </summary>
    private static FileStream? TryClaim(string file)
    {
        try
        {
            return new FileStream(file + ".claim", FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // Someone is reading it, or deleting it. It is theirs now.
        }
        catch (UnauthorizedAccessException)
        {
            // A delete that is still pending.
        }
    }

    private static long StartTicks(Process process) => process.StartTime.ToUniversalTime().Ticks;

    private string EntryPath(int serverProcessId) => Path.Combine(_folder, $"{serverProcessId}.json");

    private static string DefaultFolder() => Path.Combine(Path.GetTempPath(), "weir-contract-servers");
}
