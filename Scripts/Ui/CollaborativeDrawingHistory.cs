namespace DrawAndGuessMod.Scripts.Ui;

internal readonly record struct DrawingOperationKey(ulong SenderId, uint OperationId);

internal sealed class CollaborativeDrawingHistory<TPatch>
{
    private readonly int _maxUndoStepsPerSender;
    private readonly List<Entry> _entries = new();
    private uint _nextSequence = 1u;

    public CollaborativeDrawingHistory(int maxUndoStepsPerSender)
    {
        if (maxUndoStepsPerSender <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUndoStepsPerSender));
        }
        _maxUndoStepsPerSender = maxUndoStepsPerSender;
    }

    public Entry Commit(DrawingOperationKey operation, TPatch patch)
    {
        Entry entry = new(operation, _nextSequence++, patch);
        if (_nextSequence == 0u)
        {
            _nextSequence = 1u;
        }
        _entries.Add(entry);
        LimitUndoHistoryFor(operation.SenderId);
        return entry;
    }

    public bool TryUndoLatest(ulong senderId, out Entry entry)
    {
        entry = _entries.LastOrDefault(candidate =>
            candidate.Active &&
            candidate.CanUndo &&
            candidate.Operation.SenderId == senderId)!;
        if (entry == null)
        {
            return false;
        }

        entry.Active = false;
        entry.CanUndo = false;
        return true;
    }

    public IEnumerable<TPatch> GetActivePatches()
    {
        return _entries
            .Where(entry => entry.Active)
            .Select(entry => entry.Patch);
    }

    public int GetUndoableCount(ulong senderId)
    {
        return _entries.Count(entry =>
            entry.Active &&
            entry.CanUndo &&
            entry.Operation.SenderId == senderId);
    }

    private void LimitUndoHistoryFor(ulong senderId)
    {
        List<Entry> undoable = _entries
            .Where(entry =>
                entry.Active &&
                entry.CanUndo &&
                entry.Operation.SenderId == senderId)
            .ToList();
        for (int index = 0; index < undoable.Count - _maxUndoStepsPerSender; index++)
        {
            undoable[index].CanUndo = false;
        }
    }

    internal sealed class Entry
    {
        public DrawingOperationKey Operation { get; }
        public uint Sequence { get; }
        public TPatch Patch { get; }
        public bool Active { get; set; } = true;
        public bool CanUndo { get; set; } = true;

        public Entry(DrawingOperationKey operation, uint sequence, TPatch patch)
        {
            Operation = operation;
            Sequence = sequence;
            Patch = patch;
        }
    }
}
