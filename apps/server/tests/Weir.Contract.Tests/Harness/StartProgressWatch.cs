using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// Judges a starting server by whether it is still making progress, the way the Windows tray does (docs/start-supervision.md): a
/// start is hung when its progress record has not changed for longer than the silence allowed, not when a fixed time has passed. A
/// start on a busy machine takes longer and keeps reporting; only one that has stopped reporting is given up on. The record is
/// the server's <c>startup-progress.json</c>; a record that does not carry the process's own id is an earlier server's and is
/// ignored.
/// </summary>
internal sealed class StartProgressWatch
{
    /// <summary>How long a server may take to make its first report, and to make the next one when it has not asked for longer.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);

    /// <summary>The longest silence a server's own request for patience is believed for.</summary>
    public static readonly TimeSpan PatienceCeiling = TimeSpan.FromHours(1);

    private readonly int _processId;
    private readonly TimeProvider _clock;
    private string? _last;
    private long _changedAt;
    private TimeSpan _allowed = Silence;

    public StartProgressWatch(int processId, TimeProvider clock)
    {
        _processId = processId;
        _clock = clock;
        _changedAt = clock.GetTimestamp();
    }

    /// <summary>What the last record said the server was doing, for a message; null before the first.</summary>
    public string? Phase { get; private set; }

    /// <summary>Takes the progress file's text as read now, or null when it could not be read or does not exist.</summary>
    public void Observe(string? record)
    {
        if (record is null || record == _last || Parse(record) is not { } parsed)
        {
            return;
        }

        _last = record;
        _changedAt = _clock.GetTimestamp();
        Phase = parsed.Phase;
        _allowed = parsed.Patience > Silence ? TimeSpan.FromTicks(Math.Min(parsed.Patience.Ticks, PatienceCeiling.Ticks)) : Silence;
    }

    /// <summary>How long the record has been the same, and whether that is longer than the server may be silent.</summary>
    public bool IsHung(out TimeSpan silentFor)
    {
        silentFor = _clock.GetElapsedTime(_changedAt);
        return silentFor > _allowed;
    }

    private (string Phase, TimeSpan Patience)? Parse(string record)
    {
        try
        {
            if (JsonNode.Parse(record) is not JsonObject fields || (int?)fields["pid"] != _processId)
            {
                return null;
            }

            return ((string?)fields["phase"] ?? "unknown", TimeSpan.FromSeconds((double?)fields["expect_within_s"] ?? 0));
        }
        catch (Exception unreadable) when (unreadable is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // A record caught half written is read again at the next look.
            return null;
        }
    }
}
