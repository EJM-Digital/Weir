using Weir.Api.Endpoints;
using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Logs;

namespace Weir.Api.Tests.Platform;

/// <summary>A log export written a page at a time reads exactly as the same export written whole.</summary>
public sealed class SystemLogExportWriterTests
{
    private static readonly IReadOnlyDictionary<long, string> NoNames = new Dictionary<long, string>();

    private static SystemLogRow Row(int key) => new(
        SystemLogSource.Event,
        key,
        new DateTimeOffset(2026, 5, 9, 10, 0, key, TimeSpan.Zero),
        "success",
        "scans",
        null,
        $"Scan {key}, with a comma",
        null,
        new WireObject().Set("detail", $"line one\nline two {key}"));

    private static string Written(SystemLogExportWriter writer, params int[][] pages) =>
        writer.Start() + string.Concat(pages.Select(page => writer.Rows([.. page.Select(Row)], NoNames))) + writer.End();

    [Fact]
    public void A_json_export_in_pages_is_the_same_text_as_the_whole_list_written_at_once()
    {
        var whole = WireJsonWriter.Dumps(
            new WireArray(Enumerable.Range(1, 5).Select(key => (WireValue)SystemLogWire.Row(Row(key), NoNames))),
            ActivityHistory.ExportJsonFormat);

        var inPages = Written(new SystemLogExportWriter(json: true), [1, 2], [], [3, 4, 5]);

        Assert.Equal(whole, inPages);
    }

    [Fact]
    public void A_csv_export_in_pages_is_the_same_text_as_the_whole_list_written_at_once()
    {
        var whole = Written(new SystemLogExportWriter(json: false), [1, 2, 3, 4, 5]);

        var inPages = Written(new SystemLogExportWriter(json: false), [1, 2], [3], [4, 5]);

        Assert.Equal(whole, inPages);
        Assert.Equal(6, inPages.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void An_export_with_no_rows_is_an_empty_json_list_or_a_csv_header()
    {
        Assert.Equal("[]", Written(new SystemLogExportWriter(json: true), []));
        Assert.Equal("time,source,level,category,workflow,title,detail\r\n", Written(new SystemLogExportWriter(json: false), []));
    }

    private static SystemLogRow RowOf(SystemLogSource source, long? workflow, string? detail, WireObject record) =>
        new(source, 1, new DateTimeOffset(2026, 5, 9, 10, 0, 0, TimeSpan.Zero), "error", "scans", workflow, "Title", detail, record);

    [Fact]
    public void A_csv_line_names_the_workflow_when_it_is_known_by_id_otherwise_and_leaves_it_empty_when_there_is_none()
    {
        var names = new Dictionary<long, string> { [7] = "Films" };
        var record = new WireObject();

        var named = SystemLogWire.CsvLines([RowOf(SystemLogSource.Event, 7, null, record)], names);
        var unnamed = SystemLogWire.CsvLines([RowOf(SystemLogSource.Event, 8, null, record)], names);
        var none = SystemLogWire.CsvLines([RowOf(SystemLogSource.Event, null, null, record)], names);

        Assert.Contains(",scans,Films,Title,", named, StringComparison.Ordinal);
        Assert.Contains(",scans,8,Title,", unnamed, StringComparison.Ordinal);
        Assert.Contains(",scans,,Title,", none, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SystemLogSource.Event, "detail")]
    [InlineData(SystemLogSource.Job, "last_error")]
    [InlineData(SystemLogSource.Server, "traceback")]
    public void The_detail_column_is_the_rows_own_record_then_its_summary_then_nothing(SystemLogSource source, string field)
    {
        var withRecord = RowOf(source, null, "summary", new WireObject().Set(field, "from the record"));
        var nullRecord = RowOf(source, null, "summary", new WireObject().Set(field, WireNull.Instance));
        var absent = RowOf(source, null, "summary", new WireObject());
        var nothing = RowOf(source, null, null, new WireObject());

        Assert.EndsWith(",from the record\r\n", SystemLogWire.CsvLines([withRecord], NoNames), StringComparison.Ordinal);
        Assert.EndsWith(",summary\r\n", SystemLogWire.CsvLines([nullRecord], NoNames), StringComparison.Ordinal);
        Assert.EndsWith(",summary\r\n", SystemLogWire.CsvLines([absent], NoNames), StringComparison.Ordinal);
        Assert.EndsWith(",\r\n", SystemLogWire.CsvLines([nothing], NoNames), StringComparison.Ordinal);
    }
}
