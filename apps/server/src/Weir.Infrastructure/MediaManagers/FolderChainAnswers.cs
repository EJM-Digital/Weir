using Weir.Core.Json;
using Weir.Infrastructure.Activity;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// The folder-chain answer each saved workflow was last given, by whoever asked: a screen reading the workflow, or the server's own
/// look while a browser watches (<see cref="FolderChecksTask"/>). An answer that is not the one last given, or is the first, is
/// told to every open stream on <see cref="DataTopics.FolderChecks"/>. A screen reads on its own as well as when it is told, so
/// the server's look has to compare with what the screens were given, not only with what it saw last: a folder that went and came
/// back between two looks leaves the look nothing to tell, and a screen that read it gone would stay red.
/// </summary>
public sealed class FolderChainAnswers
{
    private readonly DataChangePublisher _changes;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, string> _last = [];

    public FolderChainAnswers(DataChangePublisher changes) => _changes = changes ?? throw new ArgumentNullException(nameof(changes));

    /// <summary>Remembers <paramref name="chain"/> as the answer for the workflow, and tells the open streams when it is not the one given before.</summary>
    public void Given(long libraryId, WireObject chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var answer = WireJsonWriter.Dumps(chain, WireJsonFormat.Compact);
        bool given;
        lock (_gate)
        {
            given = _last.TryGetValue(libraryId, out var before);
            if (given && before == answer)
            {
                return;
            }

            _last[libraryId] = answer;
        }

        if (given)
        {
            _changes.Publish(DataTopics.FolderChecks);
        }
    }

    /// <summary>Forgets the workflows that are not in <paramref name="kept"/>, so one that comes back starts afresh.</summary>
    public void KeepOnly(IReadOnlySet<long> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        lock (_gate)
        {
            foreach (var departed in _last.Keys.Where(id => !kept.Contains(id)).ToList())
            {
                _last.Remove(departed);
            }
        }
    }
}
