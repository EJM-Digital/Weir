using Microsoft.Data.Sqlite;
using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Settings;

/// <summary>The key a backup's secrets are sealed with, and the salt that went into making it.</summary>
public sealed record BackupKey(byte[] Key, byte[] Salt);

/// <summary>
/// The backup password. Weir keeps the key the password makes (never the password), locked with the install's credentials secret
/// like every other stored secret, so the schedule can seal a backup while nobody is signed in.
/// </summary>
public sealed class BackupPasswordStore
{
    private readonly CredentialCipher _cipher;
    private readonly DataChangePublisher? _changes;

    /// <summary>Once a new password commits, <paramref name="changes"/> tells every open Backups screen.</summary>
    public BackupPasswordStore(CredentialCipher cipher, DataChangePublisher? changes = null)
    {
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
        _changes = changes;
    }

    public async Task<bool> IsSetAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return await uow.QuerySingleAsync("SELECT salt, key_ciphertext FROM backup_password WHERE id = 1", Read).ConfigureAwait(false) is not null;
    }

    /// <summary>Sets or changes the backup password. Throws <see cref="WireValueException"/> with the reason when the password is too weak.</summary>
    public async Task SetAsync(UnitOfWork uow, string password)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(password);
        if (PasswordPolicy.Validate(password, username: null) is { } reason)
        {
            throw new WireValueException(reason);
        }

        var salt = BackupSecretsSeal.NewSalt();
        var key = BackupSecretsSeal.DeriveKey(password, salt);
        await uow.ExecuteAsync(
            "INSERT INTO backup_password (id, salt, key_ciphertext) VALUES (1, $salt, $key) " +
            "ON CONFLICT (id) DO UPDATE SET salt = excluded.salt, key_ciphertext = excluded.key_ciphertext, updated_at = CURRENT_TIMESTAMP",
            ("$salt", Convert.ToBase64String(salt)),
            ("$key", _cipher.Encrypt(Convert.ToBase64String(key)))).ConfigureAwait(false);
        _changes?.PublishOnCommit(uow, DataTopics.Backups);
    }

    /// <summary>
    /// The key to seal the next backup with, or <see langword="null"/> when no backup password is set. Throws
    /// <see cref="WireValueException"/> when one is set but Weir can no longer open it.
    /// </summary>
    public async Task<BackupKey?> GetKeyAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var stored = await uow.QuerySingleAsync("SELECT salt, key_ciphertext FROM backup_password WHERE id = 1", Read).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var key = _cipher.Decrypt(stored.Ciphertext);
        if (key is null)
        {
            throw new WireValueException("Weir cannot read the backup password it saved. Set the backup password again on System, Backups.");
        }

        return new BackupKey(Convert.FromBase64String(key), Convert.FromBase64String(stored.Salt));
    }

    private sealed record Stored(string Salt, string Ciphertext);

    private static Stored Read(SqliteDataReader reader) =>
        new(SqliteValues.GetString(reader, 0), SqliteValues.GetString(reader, 1));
}
