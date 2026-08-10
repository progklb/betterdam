using System.Collections.Concurrent;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;

namespace BetterDAM.Core.Services;

public sealed class PendingChangeStore : IPendingChangeStore
{
    private readonly ConcurrentDictionary<string, PendingChange> _changes = new(StringComparer.Ordinal);

    public int Count => _changes.Count;

    public event EventHandler<PendingChangesChangedEventArgs>? Changed;

    public void Set(string filePath, EditableMetadata original, EditableMetadata edited)
    {
        if (edited.ValueEquals(original))
        {
            Discard(filePath);
            return;
        }

        _changes[filePath] = new PendingChange(filePath, original, edited);
        Changed?.Invoke(this, new PendingChangesChangedEventArgs(filePath));
    }

    public EditableMetadata? GetEdited(string filePath)
        => _changes.TryGetValue(filePath, out var change) ? change.Edited : null;

    public bool HasChanges(string filePath) => _changes.ContainsKey(filePath);

    public void Discard(string filePath)
    {
        if (_changes.TryRemove(filePath, out _))
        {
            Changed?.Invoke(this, new PendingChangesChangedEventArgs(filePath));
        }
    }

    public void DiscardAll()
    {
        if (_changes.IsEmpty)
        {
            return;
        }

        _changes.Clear();
        Changed?.Invoke(this, new PendingChangesChangedEventArgs(null));
    }

    public IReadOnlyCollection<PendingChange> GetAll() => _changes.Values.ToList();
}
