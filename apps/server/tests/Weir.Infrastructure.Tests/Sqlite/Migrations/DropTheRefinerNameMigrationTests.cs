using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0009_drop_the_refiner_name.sql</c> rebuilds thirteen tables under new names so that a rule set, a library, a file and
/// everything that points at them keep their rows. Dropping a table whose children cascade would empty them (#578), so the order of the
/// script is the whole point. The schema is the one migrations 1 to 8 really leave; the rows are written here, in the shape those
/// migrations give them, because no published release wrote a database before this step.
/// </summary>
public sealed class DropTheRefinerNameMigrationTests : IDisposable
{
    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["refiner_rule_sets"] = "rule_sets",
        ["refiner_libraries"] = "libraries",
        ["refiner_jobs"] = "jobs",
        ["refiner_files"] = "files",
        ["refiner_file_logs"] = "file_logs",
        ["refiner_library_manager_links"] = "library_manager_links",
        ["refiner_operator_settings"] = "operator_settings",
    };

    private static readonly string[] SameName =
    [
        "library_file_facets", "library_folders", "library_swaps", "media_manager_handoffs", "removed_tracks", "library_files",
    ];

    private readonly TempDirectory _temp = new();
    private readonly string _path;
    private readonly Dictionary<string, List<string>> _before = [];

    public DropTheRefinerNameMigrationTests()
    {
        _path = _temp.Join("weir.sqlite3");
        var earlier = SchemaMigrator.Migrations.Where(migration => migration.Number <= 8).OrderBy(migration => migration.Number);
        SchemaSnapshot.Execute(_path, string.Concat(earlier.Select(SchemaMigrator.ReadMigrationSql)) + Rows);
        foreach (var table in Renamed.Keys.Concat(SameName))
        {
            _before[table] = Dump(table);
        }
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    private const string Rows = """
        INSERT INTO media_manager_connections (id, kind, name, base_url) VALUES (1, 'deluno', 'Deluno on nas', 'http://nas:7878');
        UPDATE refiner_rule_sets SET name = 'Keep English', primary_audio_lang = 'eng', secondary_audio_lang = 'fre', tertiary_audio_lang = 'ger',
            default_audio_slot = 'secondary', remove_commentary = 1, subtitle_mode = 'keep_selected', subtitle_langs_csv = 'eng,fre', keep_original_language = 1,
            original_language_additional_csv = 'jpn', remove_images = 1, remove_title = 1, remove_hearing_impaired_subs = 1, audio_keep_mode = 'per_language',
            subtitle_max_per_language = 2, track_name_template = '{language} {codec}', remove_chapters = 1 WHERE id = 1;
        UPDATE refiner_libraries SET watched_folder = '/in/movies', work_folder = '/work/movies', output_folder = '/out/movies', rule_set_id = 1, discovered_from_connection_id = 1,
            discovered_library_key = 'deluno-movies', min_file_size_mb = 50, schedule_days = 'mon,tue', max_attempts = 5, remux_writer = 'ffmpeg' WHERE id = 1;
        UPDATE refiner_libraries SET watched_folder = '/in/tv', work_folder = '/work/tv', output_folder = '/out/tv', rule_set_id = 2 WHERE id = 2;
        INSERT INTO refiner_libraries (id, name, media_type, watched_folder, work_folder, output_folder) VALUES (3, 'Spare', 'movie', '/in/spare', '/work/spare', '/out/spare');
        INSERT INTO refiner_jobs (id, dedupe_key, job_kind, payload_json, status, attempt_count, last_error)
            VALUES (1, 'processing.file.remux_pass.v1:a', 'processing.file.remux_pass.v1', '{"relative_media_path":"a.mkv"}', 'completed', 1, NULL);
        INSERT INTO refiner_jobs (id, dedupe_key, job_kind, status, attempt_count, last_error)
            VALUES (2, 'processing.library.clean.v1:b', 'processing.library.clean.v1', 'failed', 3, 'It stopped');
        INSERT INTO refiner_files (id, library_id, relative_path, status, status_reason, size_bytes, video_codec, audio_track_count, failure_attempts)
            VALUES (1, 1, 'A/a.mkv', 'processed', 'Done', 1000, 'h264', 2, 0);
        INSERT INTO refiner_files (id, library_id, relative_path, status, size_bytes) VALUES (2, 1, 'B/b.mkv', 'rejected', 2000);
        INSERT INTO refiner_files (id, library_id, relative_path, status, size_bytes) VALUES (3, 2, 'C/c.mkv', 'unprocessed', 3000);
        INSERT INTO refiner_file_logs (id, file_id, library_id, relative_path, library_name, outcome, title, detail_json) VALUES (1, 1, 1, 'A/a.mkv', 'Movies', 'success', 'Done', '{"ok":true}');
        INSERT INTO refiner_file_logs (id, file_id, library_id, relative_path, library_name, outcome, title, detail_json) VALUES (2, NULL, NULL, 'gone/g.mkv', 'Old', 'failed', 'Gone', '{}');
        INSERT INTO refiner_library_manager_links (id, library_id, connection_id) VALUES (1, 1, 1);
        UPDATE refiner_operator_settings SET runner_capacity = 3 WHERE id = 1;
        INSERT INTO library_files (id, library_id, path, size_bytes, classification, summary, video_codec, video_height, resolution_class, audio_track_count, problem_kind)
            VALUES (1, 1, '/lib/movies/a.mkv', 1000, 'would_change', 'Two audio', 'h264', 1080, '1080p', 2, NULL);
        INSERT INTO library_files (id, library_id, path, size_bytes, classification, manager_connection_id, manager_title)
            VALUES (2, 1, '/lib/movies/b.mkv', 2000, 'matches', 1, 'B');
        INSERT INTO library_files (id, library_id, path, size_bytes, classification) VALUES (3, 2, '/lib/tv/c.mkv', 3000, 'matches');
        INSERT INTO library_file_facets (id, library_id, library_file_id, facet, value) VALUES (1, 1, 1, 'audio_language', 'eng');
        INSERT INTO library_file_facets (id, library_id, library_file_id, facet, value) VALUES (2, 1, 1, 'audio_language', 'fre');
        INSERT INTO library_file_facets (id, library_id, library_file_id, facet, value) VALUES (3, 1, 2, 'audio_language', 'eng');
        INSERT INTO library_file_facets (id, library_id, library_file_id, facet, value) VALUES (4, 2, 3, 'audio_language', 'ger');
        INSERT INTO library_folders (id, library_id, folder, position) VALUES (1, 1, '/lib/movies', 0);
        INSERT INTO library_folders (id, library_id, folder, position) VALUES (2, 2, '/lib/tv', 0);
        INSERT INTO library_swaps (job_id, state, original_path, temp_path, backup_path, committed) VALUES (2, 'cleaned_copy_in_place', '/lib/movies/b.mkv', '/lib/movies/b.weir-tmp', '/lib/movies/b.weir-bak', 1);
        INSERT INTO media_manager_handoffs (id, source_key, handoff_id, library_id, relative_path, state, output_path, message) VALUES (1, 'deluno', 'hand-1', 1, 'A/a.mkv', 'completed', '/out/movies/A/a.mkv', 'Done');
        INSERT INTO media_manager_handoffs (id, source_key, handoff_id, library_id, relative_path, state) VALUES (2, 'deluno', 'hand-2', NULL, 'Z/z.mkv', 'failed');
        INSERT INTO removed_tracks (id, library_id, relative_path, language, track_type, codec, reason) VALUES (1, 1, 'A/a.mkv', 'fre', 'audio', 'ac3', 'language not kept');
        INSERT INTO removed_tracks (id, library_id, relative_path, language, track_type, codec, reason) VALUES (2, NULL, 'Q/q.mkv', 'ger', 'audio', 'aac', 'language not kept');
        """;

    private SqliteConnection Open(bool foreignKeys)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        connection.Open();
        Run(connection, $"PRAGMA foreign_keys={(foreignKeys ? "ON" : "OFF")}");
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Every row of the table, columns named, in id order, so rows before and after compare as text. The one column that carried the old name,
    /// <c>refiner_min_input_file_size_mb</c>, is read under its new one.
    /// </summary>
    private List<string> Dump(string table)
    {
        using var connection = Open(foreignKeys: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(";", Enumerable.Range(0, reader.FieldCount).Select(i =>
                $"{reader.GetName(i).Replace("refiner_", string.Empty, StringComparison.Ordinal)}={(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))}")));
        }

        return rows;
    }

    /// <summary>The migration as the migrator runs it: foreign keys off, one transaction, a foreign key check before the commit.</summary>
    private void Migrate()
    {
        using var connection = Open(foreignKeys: false);
        using var transaction = connection.BeginTransaction();
        using (var script = connection.CreateCommand())
        {
            script.Transaction = transaction;
            script.CommandText = SchemaMigrator.ReadMigrationSql(SchemaMigrator.Migrations.Single(migration => migration.Number == 9));
            script.ExecuteNonQuery();
        }

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "PRAGMA foreign_key_check";
            using var violations = check.ExecuteReader();
            Assert.False(violations.Read(), "the rebuild left a row that points at nothing");
        }

        transaction.Commit();
    }

    [Fact]
    public void The_rows_to_begin_with_are_all_there_so_the_comparison_means_something()
    {
        Assert.All(_before.Values, rows => Assert.NotEmpty(rows));
        Assert.Equal(13, _before.Count);
    }

    [Theory]
    [InlineData("refiner_rule_sets")]
    [InlineData("refiner_libraries")]
    [InlineData("refiner_jobs")]
    [InlineData("refiner_files")]
    [InlineData("refiner_file_logs")]
    [InlineData("refiner_library_manager_links")]
    [InlineData("refiner_operator_settings")]
    public void A_table_that_lost_its_name_keeps_every_row_and_every_value(string old)
    {
        Migrate();

        Assert.Equal(_before[old], Dump(Renamed[old]));
    }

    [Theory]
    [InlineData("library_file_facets")]
    [InlineData("library_folders")]
    [InlineData("library_swaps")]
    [InlineData("media_manager_handoffs")]
    [InlineData("removed_tracks")]
    [InlineData("library_files")]
    public void A_table_that_was_rebuilt_only_because_it_points_at_one_keeps_every_row_and_every_value(string table)
    {
        Migrate();

        Assert.Equal(_before[table], Dump(table));
    }

    [Fact]
    public void No_table_or_index_is_left_with_the_old_name_and_every_index_is_back()
    {
        Migrate();
        using var connection = Open(foreignKeys: false);

        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%refiner%' OR sql LIKE '%refiner%'"));
        var expected = Regex.Matches(SchemaMigrator.ReadMigrationSql(SchemaMigrator.Migrations.Single(migration => migration.Number == 9)), @"CREATE (?:UNIQUE )?INDEX (\w+) ON (\w+)")
            .Select(match => $"{match.Groups[1].Value} on {match.Groups[2].Value}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(16, expected.Count);
        using var listed = connection.CreateCommand();
        listed.CommandText = "SELECT name || ' on ' || tbl_name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix\\_%' ESCAPE '\\' AND tbl_name != 'activity_events' ORDER BY name";
        using var reader = listed.ExecuteReader();
        var found = new List<string>();
        while (reader.Read())
        {
            found.Add(reader.GetString(0));
        }

        Assert.Equal(expected, found.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void The_constraints_carry_the_new_names_and_point_at_the_new_tables()
    {
        Migrate();
        using var connection = Open(foreignKeys: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'libraries'";
        var sql = (string)command.ExecuteScalar()!;

        Assert.Contains("CONSTRAINT pk_libraries PRIMARY KEY (id)", sql, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT uq_libraries_name UNIQUE (name)", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES rule_sets (id) ON DELETE RESTRICT", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES media_manager_connections (id) ON DELETE SET NULL", sql, StringComparison.Ordinal);
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('library_files') WHERE \"table\" = 'libraries' AND on_delete = 'CASCADE'"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('library_swaps') WHERE \"table\" = 'jobs' AND on_delete = 'CASCADE'"));
    }

    [Fact]
    public void With_foreign_keys_on_nothing_points_at_nothing_and_removing_a_workflow_still_takes_exactly_its_own_rows()
    {
        Migrate();
        using var connection = Open(foreignKeys: true);
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));

        Run(connection, "DELETE FROM libraries WHERE id = 1");

        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM files"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM library_files"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM library_file_facets"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM library_folders"));
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM library_manager_links"));
        Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM file_logs WHERE library_id IS NULL"));
        Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM media_manager_handoffs WHERE library_id IS NULL"));
        Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM removed_tracks WHERE library_id IS NULL"));
    }

    [Fact]
    public void A_rule_set_a_workflow_uses_still_cannot_be_removed_and_a_job_takes_its_swap_with_it()
    {
        Migrate();
        using var connection = Open(foreignKeys: true);

        var refused = Assert.Throws<SqliteException>(() => Run(connection, "DELETE FROM rule_sets WHERE id = 1"));
        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.Ordinal);
        Run(connection, "DELETE FROM jobs WHERE id = 2");
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM library_swaps"));
    }

    [Fact]
    public void The_database_passes_its_own_integrity_check_after_the_rebuild()
    {
        Migrate();
        using var connection = Open(foreignKeys: true);

        Assert.Equal("ok", Convert.ToString(new SqliteCommand("PRAGMA integrity_check", connection).ExecuteScalar(), CultureInfo.InvariantCulture));
    }
}
