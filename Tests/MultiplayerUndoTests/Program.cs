using DrawAndGuessMod.Scripts.Ui;

RunSelectiveUndoScenario();
RunCoveredStrokeScenario();
RunFixedFillPatchScenario();
RunPerSenderHistoryLimitScenario();
Console.WriteLine("All multiplayer undo history tests passed.");

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
