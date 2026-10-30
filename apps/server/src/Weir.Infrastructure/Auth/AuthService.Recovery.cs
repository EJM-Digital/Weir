using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Auth;

/// <summary>An account that has just got back in with a recovery code, and how many codes it has left.</summary>
public sealed record RecoveredAccount(UserRecord User, int CodesLeft);

/// <summary>The account's recovery codes: making a set, saying it is saved, and getting back in with one.</summary>
public sealed partial class AuthService
{
    public Task<RecoveryCodeStatus> RecoveryCodeStatusAsync(UnitOfWork uow, long userId) => _recovery.StatusAsync(uow, userId);

    /// <summary>Makes the first set for an account that has none, or a new set that voids the old one. The codes are returned once and never kept.</summary>
    public async Task<IReadOnlyList<string>> MakeRecoveryCodesAsync(UnitOfWork uow, long userId)
    {
        var codes = RecoveryCodeRules.NewSet();
        var hashes = await Task.WhenAll(codes.Select(code => HashAsync(code, PasswordHasher.HashRandomSecret))).ConfigureAwait(false);
        var rows = codes.Select((code, index) => (code[..5], hashes[index])).ToList();
        await _recovery.ReplaceSetAsync(uow, userId, rows, Now()).ConfigureAwait(false);
        return codes;
    }

    /// <summary>The person has said they saved the codes they were shown.</summary>
    public Task<int> ConfirmRecoveryCodesAsync(UnitOfWork uow, long userId) => _recovery.ConfirmAsync(uow, userId, Now());

    /// <summary>
    /// Spends the typed recovery code and ends every session of its account. Null when the code is not one, is already used, or
    /// does not match: a missing code costs the same time as a wrong one, so the wait does not say which codes exist.
    /// </summary>
    public Task<RecoveredAccount?> RedeemRecoveryCodeAsync(UnitOfWork uow, string typed) => RedeemRecoveryCodeAsync(uow, typed, afterVerified: null);

    internal async Task<RecoveredAccount?> RedeemRecoveryCodeAsync(UnitOfWork uow, string typed, Func<Task>? afterVerified)
    {
        RecoveryCodeRecord? row = null;
        if (RecoveryCodeRules.TryParse(typed, out var code, out var lookup))
        {
            row = await _recovery.FindUnusedAsync(uow, lookup).ConfigureAwait(false);
        }

        if (row is null)
        {
            await VerifyPasswordAsync(code, PasswordHasher.DummyRandomSecretHash).ConfigureAwait(false);
            return null;
        }

        if (!await VerifyPasswordAsync(code, row.CodeHash).ConfigureAwait(false))
        {
            return null;
        }

        if (afterVerified is not null)
        {
            await afterVerified().ConfigureAwait(false);
        }

        if (!await _recovery.MarkUsedAsync(uow, row.Id, Now()).ConfigureAwait(false))
        {
            return null;
        }

        await _users.DeleteSessionsForUserAsync(uow, row.UserId).ConfigureAwait(false);
        var user = (await _users.GetUserAsync(uow, row.UserId).ConfigureAwait(false))!;
        var status = await _recovery.StatusAsync(uow, row.UserId).ConfigureAwait(false);
        return new RecoveredAccount(user, status.Remaining);
    }

    /// <summary>The token a spent code gives for choosing a new password.</summary>
    public string IssueRecoveryResetToken(string secret, UserRecord user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return RecoveryResetTokens.Issue(secret, user.Id, user.PasswordHash, _time);
    }

    /// <summary>
    /// Sets the new password for the account a spent code opened, and returns its username. Throws <see cref="WireValueException"/>
    /// with the operator message when the token has run out, was used already, or the password is too weak.
    /// </summary>
    public async Task<string> ResetPasswordAfterRecoveryAsync(UnitOfWork uow, string secret, string token, string newPassword)
    {
        var user = RecoveryResetTokens.TryRead(secret, token, _time, out var userId, out var passwordTag)
            ? await _users.GetUserAsync(uow, userId).ConfigureAwait(false)
            : null;
        if (user is null || RecoveryResetTokens.PasswordTag(user.PasswordHash) != passwordTag)
        {
            throw new WireValueException(RecoveryCodeMessages.ResetTimedOut);
        }

        if (PasswordPolicy.Validate(newPassword, user.Username) is { } problem)
        {
            throw new WireValueException(problem);
        }

        await _users.UpdatePasswordHashAsync(uow, user.Id, await HashAsync(newPassword, PasswordHasher.Hash).ConfigureAwait(false)).ConfigureAwait(false);
        return user.Username;
    }

    /// <summary>Hashes inside <see cref="_argon2Concurrency"/>, off the request's thread, so a set of codes is made in parallel without a burst of memory.</summary>
    private async Task<string> HashAsync(string plain, Func<string, string> hash)
    {
        await _argon2Concurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => hash(plain)).ConfigureAwait(false);
        }
        finally
        {
            _argon2Concurrency.Release();
        }
    }
}
