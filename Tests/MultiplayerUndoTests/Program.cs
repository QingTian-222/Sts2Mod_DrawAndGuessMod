using DrawAndGuessMod.Scripts.Ui;

RunSelectiveUndoScenario();
RunCoveredStrokeScenario();
RunFixedFillPatchScenario();
RunPerSenderHistoryLimitScenario();
RunRedoOrderScenario();
RunRedoBranchInvalidationScenario();
RunOtherPlayerCommitPreservesRedoScenario();
RunCoveredStrokeRedoScenario();
Console.WriteLine("All multiplayer undo/redo history tests passed.");

static void RunSelectiveUndoScenario()
{
    CollaborativeDrawingHistory<string> history = new(20);
    CollaborativeDrawingHistory<string>.Entry first =
        history.Commit(new DrawingOperationKey(1, 1), "A1");
    CollaborativeDrawingHistory<string>.Entry second =
        history.Commit(new DrawingOperationKey(2, 1), "B1");
    CollaborativeDrawingHistory<string>.Entry third =
        history.Commit(new DrawingOperationKey(1, 2), "A2");

    Assert(first.Sequence < second.Sequence && second.Sequence < third.Sequence, "Global sequence must be monotonic.");
    Assert(history.TryUndoLatest(1, out CollaborativeDrawingHistory<string>.Entry undone), "Player A should be able to undo.");
    Assert(undone.Operation == new DrawingOperationKey(1, 2), "Player A must undo only their latest operation.");
    Assert(history.GetActivePatches().SequenceEqual(["A1", "B1"]), "Player B's operation must remain active.");
    Assert(!history.TryUndoLatest(3, out _), "A player without history must not undo another player's operation.");
}

static void RunCoveredStrokeScenario()
{
    CollaborativeDrawingHistory<TestPatch> history = new(20);
    history.Commit(new DrawingOperationKey(1, 1), new TestPatch((1, "red"), (2, "red")));
    history.Commit(new DrawingOperationKey(2, 1), new TestPatch((2, "blue"), (3, "blue")));

    Dictionary<int, string> beforeUndo = Render(history.GetActivePatches());
    Assert(beforeUndo[2] == "blue", "The later blue stroke should cover the red stroke.");

    Assert(history.TryUndoLatest(2, out _), "Player B should be able to undo the blue stroke.");
    Dictionary<int, string> afterUndo = Render(history.GetActivePatches());
    Assert(afterUndo[2] == "red", "Undoing the covering stroke should reveal the earlier red stroke.");
}

static void RunFixedFillPatchScenario()
{
    CollaborativeDrawingHistory<TestPatch> history = new(20);
    history.Commit(new DrawingOperationKey(1, 1), new TestPatch((5, "border")));
    history.Commit(new DrawingOperationKey(2, 1), new TestPatch((6, "fill"), (7, "fill")));

    Assert(history.TryUndoLatest(1, out _), "Player A should be able to undo the earlier border.");
    Dictionary<int, string> rendered = Render(history.GetActivePatches());
    Assert(rendered.Count == 2 && rendered[6] == "fill" && rendered[7] == "fill",
        "A committed fill must keep its original pixel mask after an earlier operation is undone.");
    Assert(history.TryUndoLatest(2, out _), "Player B should be able to undo the fixed fill patch.");
    Assert(history.TryRedoLatest(2, out _), "Player B should be able to redo the fixed fill patch.");
    Dictionary<int, string> redone = Render(history.GetActivePatches());
    Assert(redone.Count == 2 && redone[6] == "fill" && redone[7] == "fill",
        "Redoing a fill must restore the original committed pixel mask without recalculating its region.");
}

static void RunPerSenderHistoryLimitScenario()
{
    CollaborativeDrawingHistory<string> history = new(2);
    history.Commit(new DrawingOperationKey(1, 1), "A1");
    history.Commit(new DrawingOperationKey(1, 2), "A2");
    history.Commit(new DrawingOperationKey(2, 1), "B1");
    history.Commit(new DrawingOperationKey(1, 3), "A3");

    Assert(history.GetUndoableCount(1) == 2, "Player A should retain only two undoable operations.");
    Assert(history.GetUndoableCount(2) == 1, "Player B's history limit must be independent.");
    Assert(history.TryUndoLatest(1, out _), "Player A should undo A3.");
    Assert(history.TryUndoLatest(1, out _), "Player A should undo A2.");
    Assert(!history.TryUndoLatest(1, out _), "A1 should be outside Player A's undo window.");
    Assert(history.TryUndoLatest(2, out _), "Player B should still be able to undo B1.");
}

static void RunRedoOrderScenario()
{
    CollaborativeDrawingHistory<string> history = new(20);
    history.Commit(new DrawingOperationKey(1, 1), "A1");
    history.Commit(new DrawingOperationKey(1, 2), "A2");
    history.Commit(new DrawingOperationKey(1, 3), "A3");

    Assert(history.TryUndoLatest(1, out _), "Player A should undo A3.");
    Assert(history.TryUndoLatest(1, out _), "Player A should undo A2.");
    Assert(history.GetRedoableCount(1) == 2, "Player A should have two redoable operations.");
    Assert(history.TryRedoLatest(1, out CollaborativeDrawingHistory<string>.Entry firstRedo),
        "Player A should redo A2 first.");
    Assert(firstRedo.Operation == new DrawingOperationKey(1, 2), "Redo order must be the reverse of undo order.");
    Assert(history.TryRedoLatest(1, out CollaborativeDrawingHistory<string>.Entry secondRedo),
        "Player A should redo A3 second.");
    Assert(secondRedo.Operation == new DrawingOperationKey(1, 3), "The newest operation should be restored last.");
    Assert(history.GetActivePatches().SequenceEqual(["A1", "A2", "A3"]),
        "Redo must restore operations in their original global sequence.");
}

static void RunRedoBranchInvalidationScenario()
{
    CollaborativeDrawingHistory<string> history = new(20);
    history.Commit(new DrawingOperationKey(1, 1), "A1");
    history.Commit(new DrawingOperationKey(1, 2), "A2");

    Assert(history.TryUndoLatest(1, out _), "Player A should undo A2.");
    history.Commit(new DrawingOperationKey(1, 3), "A3");
    Assert(history.GetRedoableCount(1) == 0, "A new Player A operation must clear only Player A's redo branch.");
    Assert(!history.TryRedoLatest(1, out _), "An invalidated redo branch must not be restorable.");
    Assert(history.GetActivePatches().SequenceEqual(["A1", "A3"]), "The new branch should remain active.");
}

static void RunOtherPlayerCommitPreservesRedoScenario()
{
    CollaborativeDrawingHistory<string> history = new(20);
    history.Commit(new DrawingOperationKey(1, 1), "A1");
    history.Commit(new DrawingOperationKey(1, 2), "A2");

    Assert(history.TryUndoLatest(1, out _), "Player A should undo A2.");
    history.Commit(new DrawingOperationKey(2, 1), "B1");
    Assert(history.GetRedoableCount(1) == 1, "Player B drawing must not clear Player A's redo branch.");
    Assert(history.TryRedoLatest(1, out _), "Player A should still be able to redo A2.");
    Assert(history.GetActivePatches().SequenceEqual(["A1", "A2", "B1"]),
        "A redone operation must return to its original position below later operations.");
}

static void RunCoveredStrokeRedoScenario()
{
    CollaborativeDrawingHistory<TestPatch> history = new(20);
    history.Commit(new DrawingOperationKey(1, 1), new TestPatch((1, "red"), (2, "red")));
    history.Commit(new DrawingOperationKey(2, 1), new TestPatch((2, "blue"), (3, "blue")));

    Assert(history.TryUndoLatest(1, out _), "Player A should undo the covered red stroke.");
    Assert(history.TryRedoLatest(1, out _), "Player A should redo the covered red stroke.");
    Dictionary<int, string> rendered = Render(history.GetActivePatches());
    Assert(rendered[2] == "blue",
        "Redoing an earlier covered stroke must not place it above Player B's later stroke.");
}

static Dictionary<int, string> Render(IEnumerable<TestPatch> patches)
{
    Dictionary<int, string> pixels = new();
    foreach (TestPatch patch in patches)
    {
        foreach ((int index, string color) in patch.Pixels)
        {
            pixels[index] = color;
        }
    }
    return pixels;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record TestPatch(params (int Index, string Color)[] Pixels);
