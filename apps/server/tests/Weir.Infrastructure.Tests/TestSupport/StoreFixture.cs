using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Configuration;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Platform;

/// <summary>A temporary WEIR_HOME with a database at head, the options for it and a clock the test moves.</summary>
internal sealed class StoreFixture : IDisposable
{
    public StoreFixture(params (string Name, string Value)[] variables)
    {
        Home = new TempDirectory();
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WEIR_HOME"] = Home.Path,
            ["WEIR_SESSION_SECRET"] = "store-tests-session-secret-0123456789",
        };
        foreach (var (name, value) in variables)
        {
            dictionary[name] = value;
        }

        Options = WeirOptionsLoader.Load(new RuntimeEnvironment(dictionary, OperatingSystem.IsWindows(), Home.Path, Home.Path));
        RuntimeDirectories.Ensure(Options);
        MigratedDatabaseTemplate.CopyTo(Options.DbPath);
        Database = new SqliteDatabase(Options.DbPath);
        new SchemaMigrator(Database).EnsureAtHead();
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
        Users = new AuthStore();
        Auth = new AuthService(Options, Clock, Database, Users, new RecoveryCodeStore(), NullLoggerFactory.Instance);
    }

    public TempDirectory Home { get; }

    public WeirOptions Options { get; }

    public SqliteDatabase Database { get; }

    public FakeTimeProvider Clock { get; }

    public AuthStore Users { get; }

    public AuthService Auth { get; }

    public async Task<T> WithUnitOfWork<T>(Func<UnitOfWork, Task<T>> work, bool commit = true)
    {
        var uow = await UnitOfWork.OpenAsync(Database);
        await using (uow)
        {
            var result = await work(uow);
            if (commit)
            {
                await uow.CommitAsync();
            }

            return result;
        }
    }

    public async Task<string?> ScalarText(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<long> Scalar(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task Execute(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        Database.ClearPool();
        Home.Dispose();
    }
}
