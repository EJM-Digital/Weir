using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>What the admission reader makes of the values SQLite hands back, and of jobs that name no library.</summary>
public sealed class WorkAdmissionReaderTests : IDisposable
{
    private const string Remux = "processing.file.remux_pass.v1";
    private const string Scan = "processing.watched_folder.remux_scan_dispatch.v1";
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);

    private readonly JobsTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    public static TheoryData<object?, bool> Flags => new()
    {
        { null, false },
        { DBNull.Value, false },
        { 0L, false },
        { 1L, true },
        { -3L, true },
        { "", false },
        { "  ", false },
        { "0", false },
        { " 0 ", false },
        { "1", true },
        { "true", true },
        { 0.0, false },
        { 2.5, true },
    };

    public static TheoryData<object?, long> Numbers => new()
    {
        { null, 0L },
        { DBNull.Value, 0L },
        { 7L, 7L },
        { "12", 12L },
        { 3.0, 3L },
    };

    [Theory]
    [MemberData(nameof(Flags))]
    public void A_stored_flag_is_read_whatever_type_SQLite_kept_it_as(object? stored, bool expected) =>
        Assert.Equal(expected, WorkAdmissionReader.Bool(stored));

    [Theory]
    [MemberData(nameof(Numbers))]
    public void A_stored_number_is_read_whatever_type_SQLite_kept_it_as(object? stored, long expected) =>
        Assert.Equal(expected, WorkAdmissionReader.Long(stored));

    [Fact]
    public async Task Jobs_count_in_the_files_at_once_readout_whether_or_not_they_name_a_library()
    {
        _db.SeedSuiteSettings();
        _db.Execute("INSERT INTO operator_settings (id) VALUES (1)");
        var library = _db.AddLibrary();
        var named = $$"""{"library_id":{{library}}}""";
        _db.InsertRawJob("running", Remux, ProcessingJobStatus.Leased, leaseOwner: "w1", leaseExpiresAt: "2026-08-26 15:00:00");
        _db.InsertRawJob("running-named", Remux, ProcessingJobStatus.Leased, leaseOwner: "w3", leaseExpiresAt: "2026-08-26 15:00:00", payloadJson: named);
        _db.InsertRawJob("running-upkeep", Scan, ProcessingJobStatus.Leased, leaseOwner: "w2", leaseExpiresAt: "2026-08-26 15:00:00");
        _db.InsertRawJob("queued", Remux);
        _db.InsertRawJob("queued-named", Remux, payloadJson: named);

        var readout = await _db.Store.ReadAsync((connection, transaction) => WorkAdmissionReader.ReadFilesAtOnce(connection, transaction, Now, workerSlots: 2));

        Assert.Equal((2, 2), (readout.Running, readout.Waiting));
    }
}
