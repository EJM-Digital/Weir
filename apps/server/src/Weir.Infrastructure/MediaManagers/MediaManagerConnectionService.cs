using System.Security.Cryptography;
using System.Text;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Notifications;
using Weir.Core.Security;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>A connection could not be saved as asked, with an operator-readable reason.</summary>
public sealed class MediaManagerConnectionException : Exception
{
    public MediaManagerConnectionException()
    {
    }

    public MediaManagerConnectionException(string message)
        : base(message)
    {
    }

    public MediaManagerConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Where to report a finished hand-off, and how to authenticate.</summary>
public sealed record ResolvedCallbackTarget(string BaseUrl, string? ApiKey);

/// <summary>
/// Reading and writing connections with their secrets encrypted, and resolving which managers look after a scope.
/// </summary>
public sealed class MediaManagerConnectionService
{
    private readonly WeirOptions _options;
    private readonly CredentialCipher _cipher;
    private readonly IMediaManagerPorts _ports;
    private readonly MediaManagerConnectionStore _store;

    public MediaManagerConnectionService(WeirOptions options, CredentialCipher cipher, IMediaManagerPorts ports, MediaManagerConnectionStore store)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(store);
        _options = options;
        _cipher = cipher;
        _ports = ports;
        _store = store;
    }

    public CredentialCipher Cipher => _cipher;

    public IMediaManagerPorts Ports => _ports;

    /// <summary>
    /// Create a connection, named after its kind and address (<see cref="ConnectionNaming"/>). Throws
    /// <see cref="MediaManagerConnectionException"/> for an operator mistake.
    /// </summary>
    public async Task<long> CreateAsync(
        UnitOfWork uow, string kind, string baseUrl = "", string? apiKey = null, bool enabled = true, bool downloadedScanEnabled = false,
        string? nickname = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var key = WireStrings.Strip(apiKey ?? string.Empty);
        var validKind = ValidateKind(kind);
        var validUrl = ValidateBaseUrl(baseUrl);
        var ciphertext = key.Length > 0 ? EncryptApiKey(key) : null;
        return await _store.InsertAsync(uow, validKind, enabled, validUrl, ciphertext, downloadedScanEnabled, ConnectionNicknames.Normalize(nickname)).ConfigureAwait(false);
    }

    /// <summary>
    /// Update a connection: <see langword="null"/> leaves a field alone; an empty <paramref name="apiKey"/> clears the key and a
    /// blank <paramref name="nickname"/> clears the nickname.
    /// </summary>
    public async Task UpdateAsync(
        UnitOfWork uow, MediaManagerConnectionRecord row, string? baseUrl = null, string? apiKey = null, bool? enabled = null,
        bool? downloadedScanEnabled = null, string? nickname = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        var changes = new List<(string Column, object? Value)>();
        string? validatedBaseUrl = null;
        if (baseUrl is not null)
        {
            validatedBaseUrl = ValidateBaseUrl(baseUrl);
            if (validatedBaseUrl != row.BaseUrl)
            {
                changes.Add(("base_url", validatedBaseUrl));
            }
        }

        if (enabled is { } flag && flag != row.Enabled)
        {
            changes.Add(("enabled", flag ? 1 : 0));
        }

        // A connection going live must still have an address ExternalUrlPolicy accepts, even when this call
        // only flips `enabled` and never touched base_url: the stored value could predate this check, or come
        // from a restored backup. Re-validating on every enable (not only a change of state) costs nothing and
        // needs no extra branch to get right.
        if (enabled == true)
        {
            ValidateBaseUrl(validatedBaseUrl ?? row.BaseUrl);
        }

        if (apiKey is not null)
        {
            var stripped = WireStrings.Strip(apiKey);
            var ciphertext = stripped.Length > 0 ? EncryptApiKey(stripped) : null;
            if (ciphertext != row.ApiKeyCiphertext)
            {
                changes.Add(("api_key_ciphertext", ciphertext));
            }
        }

        if (downloadedScanEnabled is { } scan && scan != row.DownloadedScanEnabled)
        {
            changes.Add(("downloaded_scan_enabled", scan ? 1 : 0));
        }

        if (ConnectionNicknames.TryChange(row.Nickname, nickname, out var wantedNickname))
        {
            changes.Add(("nickname", wantedNickname));
        }

        await _store.UpdateColumnsAsync(uow, row.Id, changes).ConfigureAwait(false);
    }

    /// <summary>A fresh inbound webhook secret, stored encrypted and returned once.</summary>
    public async Task<string> RotateWebhookSecretAsync(UnitOfWork uow, MediaManagerConnectionRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await _store.UpdateColumnsAsync(uow, row.Id, [("webhook_secret_ciphertext", _cipher.Encrypt(plaintext))]).ConfigureAwait(false);
        return plaintext;
    }

    /// <summary>Whether the presented webhook secret matches; a connection with no secret requires none.</summary>
    public bool WebhookSecretMatches(MediaManagerConnectionRecord row, string? presented)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrEmpty(row.WebhookSecretCiphertext))
        {
            return true;
        }

        var expected = _cipher.Decrypt(row.WebhookSecretCiphertext);
        return !string.IsNullOrEmpty(expected) && CompareDigest(expected, WireStrings.Strip(presented ?? string.Empty));
    }

    /// <summary>The connection's address and decrypted key for reporting back, or null when it has no address.</summary>
    public ResolvedCallbackTarget? ResolveCallbackTarget(MediaManagerConnectionRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var baseUrl = WireStrings.Strip(row.BaseUrl);
        if (baseUrl.Length == 0)
        {
            return null;
        }

        var apiKey = string.IsNullOrEmpty(row.ApiKeyCiphertext) ? null : _cipher.Decrypt(row.ApiKeyCiphertext);
        return new ResolvedCallbackTarget(baseUrl.TrimEnd('/'), apiKey);
    }

    /// <summary>Constant-time comparison of the two strings' UTF-8 bytes.</summary>
    public static bool CompareDigest(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    // --- which managers look after a scope ------------------------------------------------------

    /// <summary>The usable connection for a row: null when the row has no address or no key that decrypts.</summary>
    public ManagerConnection? ConnectionFromRow(MediaManagerConnectionRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var url = WireStrings.Strip(row.BaseUrl);
        var ciphertext = WireStrings.Strip(row.ApiKeyCiphertext ?? string.Empty);
        if (url.Length == 0 || ciphertext.Length == 0)
        {
            return null;
        }

        var key = _cipher.Decrypt(ciphertext);
        return string.IsNullOrEmpty(key) ? null : new ManagerConnection(row.Kind, row.Name, url, key, row.Id, row.Nickname);
    }

    /// <summary>Every enabled, credentialed manager for the scope; the environment only when nothing claims it.</summary>
    public async Task<List<ManagerConnection>> ConnectionsForScopeAsync(UnitOfWork uow, string mediaScope)
    {
        var rows = await _store.ListEnabledAsync(uow).ConfigureAwait(false);
        var serving = rows.Where(row => _ports.PortForKind(row.Kind) is { } port && port.Capabilities().Scopes.Contains(mediaScope)).ToList();
        var resolved = serving.Select(ConnectionFromRow).OfType<ManagerConnection>().ToList();
        if (resolved.Count > 0)
        {
            return resolved;
        }

        if (serving.Count > 0)
        {
            return [];
        }

        return HttpMediaManagerPorts.EnvironmentConnectionForScope(_options, mediaScope) is { } environment ? [environment] : [];
    }

    /// <summary>Every enabled connection that has an address and a key that decrypts.</summary>
    public async Task<List<ManagerConnection>> AllEnabledConnectionsAsync(UnitOfWork uow) =>
        [.. (await _store.ListEnabledAsync(uow).ConfigureAwait(false)).Select(ConnectionFromRow).OfType<ManagerConnection>()];

    /// <summary>Exactly the enabled connections named, in id order; half-configured ones dropped.</summary>
    public async Task<List<ManagerConnection>> ConnectionsByIdAsync(UnitOfWork uow, IEnumerable<long> connectionIds)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        var wanted = connectionIds.ToHashSet();
        if (wanted.Count == 0)
        {
            return [];
        }

        var rows = await _store.ListEnabledAsync(uow).ConfigureAwait(false);
        return [.. rows.Where(row => wanted.Contains(row.Id)).Select(ConnectionFromRow).OfType<ManagerConnection>()];
    }

    /// <summary>Every covering manager's queue, rows narrowed to the scope asked about.</summary>
    public async Task<IReadOnlyList<ManagerQueueSignal>> CollectQueueSignalsAsync(UnitOfWork uow, string mediaScope, IReadOnlyCollection<long>? connectionIds = null, CancellationToken cancellationToken = default)
    {
        var resolved = connectionIds is not null
            ? await ConnectionsByIdAsync(uow, connectionIds).ConfigureAwait(false)
            : await ConnectionsForScopeAsync(uow, mediaScope).ConfigureAwait(false);
        var signals = new List<ManagerQueueSignal>();
        foreach (var connection in resolved)
        {
            if (_ports.PortForKind(connection.Kind) is not { } port)
            {
                continue;
            }

            signals.Add(ManagerDialectRules.OnlyRowsForScope(await port.QueueRowsAsync(connection, cancellationToken).ConfigureAwait(false), mediaScope));
        }

        return signals;
    }

    /// <summary>What every covering manager says is in its library for the scope.</summary>
    public async Task<IReadOnlyList<ManagerLibraryTruth>> CollectLibraryTruthAsync(UnitOfWork uow, string mediaScope, IReadOnlyCollection<long>? connectionIds = null, CancellationToken cancellationToken = default)
    {
        var resolved = connectionIds is not null
            ? await ConnectionsByIdAsync(uow, connectionIds).ConfigureAwait(false)
            : await ConnectionsForScopeAsync(uow, mediaScope).ConfigureAwait(false);
        var answers = new List<ManagerLibraryTruth>();
        foreach (var connection in resolved)
        {
            if (_ports.PortForKind(connection.Kind) is not { } port)
            {
                continue;
            }

            answers.Add(await port.LibraryTruthAsync(connection, mediaScope, cancellationToken).ConfigureAwait(false));
        }

        return answers;
    }

    /// <summary>What each enabled, credentialed manager says it manages.</summary>
    public async Task<IReadOnlyList<ManagerDescription>> DescribeConnectionsAsync(UnitOfWork uow, CancellationToken cancellationToken = default)
    {
        var described = new List<ManagerDescription>();
        foreach (var connection in await AllEnabledConnectionsAsync(uow).ConfigureAwait(false))
        {
            if (_ports.PortForKind(connection.Kind) is not { } port)
            {
                continue;
            }

            described.Add(await port.DescribeAsync(connection, cancellationToken).ConfigureAwait(false));
        }

        return described;
    }

    /// <summary>
    /// Encryption throws a plain <see cref="WireValueException"/> when no <c>WEIR_CREDENTIALS_SECRET</c> or
    /// <c>WEIR_SESSION_SECRET</c> is configured. It becomes a <see cref="MediaManagerConnectionException"/> so the
    /// operator gets a readable 400 naming the env var to set, not a 500 (#544 item 3).
    /// </summary>
    private string EncryptApiKey(string plaintext)
    {
        try
        {
            return _cipher.Encrypt(plaintext);
        }
        catch (WireValueException exception)
        {
            throw new MediaManagerConnectionException(exception.Message, exception);
        }
    }

    private static string ValidateKind(string? kind)
    {
        var value = WireStrings.Strip(kind ?? string.Empty).ToLowerInvariant();
        if (!MediaManagerKinds.All.Contains(value))
        {
            throw new MediaManagerConnectionException(
                $"Unknown media manager kind {WireStrings.Repr(kind ?? string.Empty)}. Known kinds: {string.Join(", ", MediaManagerKinds.All)}.");
        }

        return value;
    }

    /// <summary>
    /// The stored form of a base URL, or throws when it is not empty and does not pass
    /// <see cref="ExternalUrlPolicy.NormalizeLocalServiceBaseUrl"/>. Internal so a restore (which must refuse the
    /// same addresses, not just the create/update endpoints) can reuse it without a second copy of the policy.
    /// </summary>
    internal static string ValidateBaseUrl(string? baseUrl)
    {
        var raw = WireStrings.Strip(baseUrl ?? string.Empty);
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return ExternalUrlPolicy.NormalizeLocalServiceBaseUrl(raw);
        }
        catch (WireValueException exception)
        {
            throw new MediaManagerConnectionException($"That address will not work: {exception.Message}", exception);
        }
    }
}
