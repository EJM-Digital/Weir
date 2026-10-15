using System.Security.AccessControl;
using System.Security.Cryptography;

namespace Weir.Tray;

/// <summary>
/// A secret the tray keeps in the runtime home and hands the server in its environment: session.secret signs
/// sign-in sessions, credentials.secret encrypts stored API keys. Anyone who can read one can forge a session or
/// decrypt the keys, so each file carries its own owner-only access list (<see cref="OwnerOnlyAccess"/>) rather
/// than relying on the folder's. The server is started by the tray under the same account and reads the secrets
/// from its environment, never from these files.
/// </summary>
static class SecretFile
{
    /// <summary>The shortest secret the server accepts; a shorter file is treated as never written.</summary>
    internal const int MinimumLength = 32;

    private const int RandomByteCount = 48;
    private const int WriteBufferSize = 4096;

    /// <summary>
    /// The secret in <paramref name="fileName"/>: the saved one if it is usable, with its access list tightened if
    /// it is looser than owner-only, otherwise a new one written owner-only. A usable saved secret is never
    /// replaced, because sessions and stored credentials depend on it.
    /// </summary>
    internal static string Ensure(string runtimeHome, string fileName, Action<TimeSpan>? pause = null)
    {
        var path = Path.Combine(runtimeHome, fileName);
        if (File.Exists(path))
        {
            var existing = ReadSaved(path, pause);
            if (existing.Length >= MinimumLength)
            {
                TryRestrict(path);
                return existing;
            }
            TrayLog.Write($"{path} is shorter than {MinimumLength} characters, so a new secret replaces it.");
        }

        var secret = NewSecret();
        Directory.CreateDirectory(runtimeHome);
        WriteOwnerOnly(path, secret);
        return secret;
    }

    /// <summary>
    /// Replaces the file's access list with owner-only when it allows anyone else or inherits from its folder.
    /// Returns whether it changed anything.
    /// </summary>
    internal static bool Restrict(string path)
    {
        var file = new FileInfo(path);
        if (OwnerOnlyAccess.IsOwnerOnly(file.GetAccessControl()))
        {
            return false;
        }
        file.SetAccessControl(OwnerOnlyAccess.ForFile());
        return true;
    }

    /// <summary>
    /// The saved secret, read the way a file that is replaced under a reader is (<see cref="ReplacedFile"/>): a virus scanner holding the
    /// file for a moment is looked past. A file that stays refused is an error and never a reason to write a new secret, which would
    /// sign everyone out and make the stored credentials unreadable. A file that is gone has no secret.
    /// </summary>
    private static string ReadSaved(string path, Action<TimeSpan>? pause)
    {
        var reading = ReplacedFile.ReadText(path, pause: pause);
        return reading.State switch
        {
            ReplacedFileState.Read => reading.Text.Trim(),
            ReplacedFileState.Absent => string.Empty,
            _ => throw new InvalidOperationException(
                $"Weir cannot read {path}: it belongs to another Windows account, or another program has it open. Start Weir as the account "
                + "that first ran it, have an administrator give this account access to the file, or close the program that has it open."),
        };
    }

    private static void TryRestrict(string path)
    {
        try
        {
            if (Restrict(path))
            {
                TrayLog.Write($"Restricted {path} to SYSTEM, Administrators and {Environment.UserName}.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            // Weir still starts: the secret works, it is only readable by more accounts than it should be.
            TrayLog.Write($"Could not restrict who can read {path} ({ex.Message}); it keeps its current access list.");
        }
    }

    private static void WriteOwnerOnly(string path, string secret)
    {
        // Created with its access list already in place, then renamed over any unusable old file, so the secret is
        // never on disk under the folder's permissions. A rename on one volume keeps the file's own list.
        var scratch = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():n}.tmp");
        try
        {
            using (var stream = new FileInfo(scratch).Create(
                FileMode.CreateNew, FileSystemRights.Write, FileShare.None, WriteBufferSize, FileOptions.None, OwnerOnlyAccess.ForFile()))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(secret);
            }
            File.Move(scratch, path, overwrite: true);
        }
        finally
        {
            // Only still there when the write or the rename failed.
            File.Delete(scratch);
        }
        TryRestrict(path);
    }

    private static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(RandomByteCount);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
