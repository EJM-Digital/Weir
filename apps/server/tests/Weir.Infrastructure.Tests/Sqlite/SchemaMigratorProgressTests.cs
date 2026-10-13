using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite;

/// <summary>Start-up can say which migration it is on, out of how many, because the migrator tells it before each one.</summary>
public sealed class SchemaMigratorProgressTests
{
    [Fact]
    public void A_new_database_reports_every_migration_in_order_out_of_the_total()
    {
        using var temp = new TempDirectory();
        var database = new SqliteDatabase(temp.Join("weir.sqlite3"));
        var seen = new List<(int Step, int Steps)>();

        new SchemaMigrator(database).EnsureAtHead(migrating: (step, steps) => seen.Add((step, steps)));
        database.ClearPool();

        var total = SchemaMigrator.Migrations.Count;
        Assert.Equal(Enumerable.Range(1, total).Select(step => (step, total)), seen);
    }

    [Fact]
    public void An_upgrade_reports_only_the_migrations_it_applies()
    {
        using var temp = new TempDirectory();
        var path = temp.Join("weir.sqlite3");
        var baselineDatabase = new SqliteDatabase(path);
        new SchemaMigrator(baselineDatabase).EnsureAtBaseline();
        baselineDatabase.ClearPool();
        var database = new SqliteDatabase(path);
        var seen = new List<(int Step, int Steps)>();

        new SchemaMigrator(database).EnsureAtHead(migrating: (step, steps) => seen.Add((step, steps)));
        database.ClearPool();

        var applied = SchemaMigrator.Migrations.Count - 1;
        Assert.Equal(Enumerable.Range(1, applied).Select(step => (step, applied)), seen);
    }

    [Fact]
    public void A_database_already_at_head_reports_no_migration()
    {
        using var temp = new TempDirectory();
        var path = temp.Join("weir.sqlite3");
        var first = new SqliteDatabase(path);
        new SchemaMigrator(first).EnsureAtHead();
        first.ClearPool();
        var database = new SqliteDatabase(path);
        var seen = 0;

        new SchemaMigrator(database).EnsureAtHead(migrating: (_, _) => seen++);
        database.ClearPool();

        Assert.Equal(0, seen);
    }
}
