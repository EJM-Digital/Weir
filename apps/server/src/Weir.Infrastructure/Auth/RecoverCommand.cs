using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Core.Text;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Auth;

/// <summary>
/// Recovers the Weir operator account from the server's own console when every session is locked out or
/// the password is forgotten (#454, #553). Local-only: there is deliberately no HTTP endpoint. Reaching the server's own shell (and so its database, under <c>WEIR_HOME</c>) is the proof of
/// identity; there is no email reset and no second admin to rescue the first.
/// </summary>
public static class RecoverCommand
{
    public const int ExitOk = 0;

    /// <summary>Several accounts exist and no <c>--username</c> was given to choose one.</summary>
    public const int ExitUsage = 1;

    /// <summary>Nothing to recover, an unknown account, a rejected password, or a bad command line.</summary>
    public const int ExitFailed = 2;

    /// <summary>
    /// Runs <c>Weir recover</c>. <paramref name="args"/> excludes the leading <c>recover</c> word itself.
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        RuntimeEnvironment runtime,
        TextWriter stdout,
        TextWriter stderr,
        IRecoverPasswordPrompt passwordPrompt,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(passwordPrompt);

        var parsed = RecoverArguments.Parse(args);
        if (parsed.ShowHelp)
        {
            stdout.Write(RecoverArguments.HelpText);
            return ExitOk;
        }

        if (parsed.Error is { } parseError)
        {
            stderr.WriteLine(RecoverArguments.UsageLine);
            stderr.WriteLine($"Weir recover: error: {parseError}");
            return ExitFailed;
        }

        if (parsed.Password is not null)
        {
            // A command-line argument is readable from shell history and, while the process runs, from
            // the process list of anyone else on the machine; the prompt (omit --password) avoids both.
            stderr.WriteLine("Warning: --password stays in your shell history and is visible in the process list while Weir recover runs. Omit it to be prompted instead.");
        }

        // WeirOptionsLoader.Load neither creates the runtime directories nor checks the db path, and this
        // command runs without the server's startup, so both happen here.
        var options = WeirOptionsLoader.Load(runtime);
        RuntimeDirectories.Ensure(options);
        RuntimeDirectories.AssertSqliteDbLocationUsable(options.DbPath);

        // Opening a SQLite path that is not there makes an empty database, so a folder Weir never used is refused before it is opened.
        if (!File.Exists(options.DbPath))
        {
            return ReportNoDatabase(options.DbPath, stderr);
        }

        var database = new SqliteDatabase(options.DbPath);
        var users = new AuthStore();
        var uow = await UnitOfWork.OpenAsync(database).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            if (await uow.CountAsync("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'users'").ConfigureAwait(false) == 0)
            {
                return ReportNoDatabase(options.DbPath, stderr);
            }

            var accounts = await users.ListUsersByUsernameAsync(uow).ConfigureAwait(false);

            if (parsed.List)
            {
                if (accounts.Count == 0)
                {
                    stdout.WriteLine("No accounts. Open /setup to create the first one.");
                    return ExitOk;
                }

                stdout.WriteLine($"Accounts in {options.DbPath}:");
                foreach (var row in accounts)
                {
                    stdout.WriteLine($"  {row.Username}  role={row.Role}  {(row.IsActive ? "active" : "INACTIVE")}");
                }

                return ExitOk;
            }

            if (accounts.Count == 0)
            {
                stderr.WriteLine("No accounts exist yet, so there is nothing to recover.");
                stderr.WriteLine("Open /setup in a browser to create the operator account.");
                return ExitFailed;
            }

            if (parsed.Username is null && accounts.Count > 1)
            {
                var names = string.Join(", ", accounts.Select(row => row.Username));
                stderr.WriteLine($"Several accounts exist. Pass --username. Found: {names}");
                return ExitUsage;
            }

            var user = await users.FindAccountForRecoveryAsync(uow, parsed.Username).ConfigureAwait(false);
            if (user is null)
            {
                stderr.WriteLine($"No account named {WireStrings.Repr(parsed.Username ?? string.Empty)}. Use --list to see them.");
                return ExitFailed;
            }

            int revoked;
            try
            {
                var newPassword = ReadNewPassword(parsed.Password, passwordPrompt);
                revoked = await ResetAccountPasswordAsync(uow, users, new ActivityStore(), user, newPassword, time ?? TimeProvider.System).ConfigureAwait(false);
            }
            catch (WireValueException exception)
            {
                // Strength and mismatch failures are the operator's to fix, not a crash.
                stderr.WriteLine($"Could not reset the password: {exception.Message}");
                return ExitFailed;
            }

            await uow.CommitAsync().ConfigureAwait(false);

            stdout.WriteLine($"Password reset for {WireStrings.Repr(user.Username)}.");
            if (user.Role == UserRoles.Admin)
            {
                stdout.WriteLine("The account is active and has the admin role.");
            }

            stdout.WriteLine($"{Plural.Of(revoked, "signed-in session")} {Plural.Noun(revoked, "was", "were")} ended — sign in again with the new password.");
            return ExitOk;
        }
    }

    private static int ReportNoDatabase(string databasePath, TextWriter stderr)
    {
        stderr.WriteLine($"There is no Weir database at {databasePath}, so there is nothing to recover.");
        stderr.WriteLine("If Weir keeps its data somewhere else, set WEIR_HOME to that folder and run this again.");
        return ExitFailed;
    }

    /// <summary>
    /// Resets the account's password: validate the new password, re-activate the account (an
    /// inactive sole admin is its own lockout), revoke every still-active session and record it in
    /// Activity. Returns the number of sessions revoked.
    /// </summary>
    public static async Task<int> ResetAccountPasswordAsync(UnitOfWork uow, AuthStore users, ActivityStore activity, UserRecord user, string newPassword, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(time);

        if (PasswordPolicy.Validate(newPassword, user.Username) is { } problem)
        {
            throw new WireValueException(problem);
        }

        await users.UpdatePasswordHashAsync(uow, user.Id, PasswordHasher.Hash(newPassword)).ConfigureAwait(false);
        // An inactive account cannot sign in, so recovery that left this alone would "succeed" and
        // still leave the operator locked out.
        await users.SetActiveAsync(uow, user.Id, true).ConfigureAwait(false);

        var now = Timestamp.UtcNow(time);
        var revoked = await users.RevokeActiveSessionsForUserAsync(uow, user.Id, now).ConfigureAwait(false);

        await activity.RecordAsync(
            uow,
            ActivityEventTypes.AuthPasswordChanged,
            "auth",
            "Password recovered from the server",
            $"{user.Username} — reset from the server console. {Plural.Of(revoked, "signed-in session")} {Plural.Noun(revoked, "was", "were")} ended.").ConfigureAwait(false);

        return revoked;
    }

    /// <summary>The new password: the one supplied on the command line, or read twice from the console.</summary>
    private static string ReadNewPassword(string? supplied, IRecoverPasswordPrompt prompt)
    {
        if (supplied is not null)
        {
            return supplied;
        }

        if (!prompt.IsInteractive)
        {
            throw new WireValueException("No terminal available to prompt for a password. Pass --password instead.");
        }

        var first = prompt.ReadPassword("New password: ");
        var second = prompt.ReadPassword("Repeat new password: ");
        if (first != second)
        {
            throw new WireValueException("The two passwords did not match.");
        }

        return first;
    }
}
