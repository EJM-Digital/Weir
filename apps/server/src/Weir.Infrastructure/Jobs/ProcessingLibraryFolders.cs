using Microsoft.Data.Sqlite;
using Weir.Core.Processing;

namespace Weir.Infrastructure.Jobs;

/// <summary>The folder columns of one <c>libraries</c> row.</summary>
public sealed record ProcessingLibraryFolderRow(long Id, string MediaType, int DisplayOrder, string WorkFolder, string OutputFolder);

/// <summary>
/// Reads library folders and resolves a library's work folder.
/// </summary>
public static class ProcessingLibraryFolders
{
    /// <summary>Every library, ordered by <c>display_order</c> then <c>id</c>.</summary>
    public static List<ProcessingLibraryFolderRow> List(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, media_type, display_order, work_folder, output_folder FROM libraries ORDER BY display_order, id";
        using var reader = command.ExecuteReader();
        var rows = new List<ProcessingLibraryFolderRow>();
        while (reader.Read())
        {
            rows.Add(new ProcessingLibraryFolderRow(
                reader.GetInt64(0),
                reader.GetString(1),
                (int)reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>The library by id when the payload has one, else the seeded (first-listed) library for the scope.</summary>
    public static ProcessingLibraryFolderRow? Resolve(IReadOnlyList<ProcessingLibraryFolderRow> libraries, long? libraryId, string? mediaScope)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        if (libraryId is { } id && libraries.FirstOrDefault(library => library.Id == id) is { } found)
        {
            return found;
        }

        var scope = ProcessingMediaScopes.Normalize(mediaScope);
        return libraries.FirstOrDefault(library => library.MediaType == scope);
    }

    public static string DefaultMovieWorkFolder(string weirHome) => Path.Join(Path.GetFullPath(weirHome), "processing", "processing-movie-work");

    public static string DefaultTvWorkFolder(string weirHome) => Path.Join(Path.GetFullPath(weirHome), "processing", "processing-tv-work");

    /// <summary>
    /// The library's own work folder, or the per-scope default when it has none.
    /// </summary>
    /// <remarks>
    /// Any non-empty <c>work_folder</c> is taken at face value, including an old install's default path: if
    /// the folder does not exist the caller refuses the run rather than quietly working somewhere else. Only
    /// an empty <c>work_folder</c> gets the default.
    /// </remarks>
    public static string EffectiveWorkFolder(ProcessingLibraryFolderRow library, string weirHome)
    {
        ArgumentNullException.ThrowIfNull(library);
        var raw = library.WorkFolder.Trim();
        if (raw.Length > 0)
        {
            return raw;
        }

        var scope = (string.IsNullOrEmpty(library.MediaType) ? "movie" : library.MediaType).Trim().ToLowerInvariant();
        return scope == "tv" ? DefaultTvWorkFolder(weirHome) : DefaultMovieWorkFolder(weirHome);
    }

    /// <summary>The path with a leading <c>~</c> expanded to the user profile, made absolute, for touching the filesystem.</summary>
    public static string ExpandForFilesystem(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var text = path;
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            text = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text.Length > 1 ? text[2..] : string.Empty);
        }

        return Path.GetFullPath(text);
    }
}
