namespace DrawAndGuessMod.Scripts.Ui;

internal readonly record struct DrawingOperationKey(ulong SenderId, uint OperationId);

internal sealed class CollaborativeDrawingHistory<TPatch>
{
    private readonly int _maxUndoStepsPerSender;
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<ulong, List<Entry>> _redoStacks = new();
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
        ClearRedoHistoryFor(operation.SenderId);
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
        entry.CanRedo = true;
        if (!_redoStacks.TryGetValue(senderId, out List<Entry>? redoStack))
        {
            redoStack = new List<Entry>();
            _redoStacks[senderId] = redoStack;
        }
        redoStack.Add(entry);
        return true;
    }

    public bool TryRedoLatest(ulong senderId, out Entry entry)
    {
        entry = null!;
        if (!_redoStacks.TryGetValue(senderId, out List<Entry>? redoStack))
        {
            return false;
        }

        while (redoStack.Count > 0)
        {
            int lastIndex = redoStack.Count - 1;
            Entry candidate = redoStack[lastIndex];
            redoStack.RemoveAt(lastIndex);
            if (!candidate.Active && candidate.CanRedo)
            {
                entry = candidate;
                entry.Active = true;
                entry.CanUndo = true;
                entry.CanRedo = false;
                LimitUndoHistoryFor(senderId);
                if (redoStack.Count == 0)
                {
                    _redoStacks.Remove(senderId);
                }
                return true;
            }
        }

        _redoStacks.Remove(senderId);
        return false;
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

    public int GetRedoableCount(ulong senderId)
    {
        return _redoStacks.TryGetValue(senderId, out List<Entry>? redoStack)
            ? redoStack.Count(entry => !entry.Active && entry.CanRedo)
            : 0;
    }

    private void ClearRedoHistoryFor(ulong senderId)
    {
        if (!_redoStacks.Remove(senderId, out List<Entry>? redoStack))
        {
            return;
        }

        foreach (Entry entry in redoStack)
        {
            entry.CanRedo = false;
        }
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
        public bool CanRedo { get; set; }

        public Entry(DrawingOperationKey operation, uint sequence, TPatch patch)
        {
            Operation = operation;
            Sequence = sequence;
            Patch = patch;
        }
    }
}
