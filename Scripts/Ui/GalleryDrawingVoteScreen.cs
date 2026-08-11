using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.State;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Ui;

internal partial class GalleryDrawingVoteScreen : Control
{
    private static readonly Dictionary<uint, Task<ulong>> ActiveVotes = new();
    private readonly TaskCompletionSource<ulong> _selection =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<BaseButton> _buttons = new();
    private Label _status = null!;

    public static void Reset()
    {
        ActiveVotes.Clear();
    }

    public static Task<ulong> RunAsync(
        uint sessionId,
        RunState runState,
        IReadOnlyList<GalleryDrawingSubmission> submissions)
    {
        if (ActiveVotes.TryGetValue(sessionId, out Task<ulong>? active))
        {
            return active;
        }

        Task<ulong> vote = RunInternalAsync(sessionId, runState, submissions);
        ActiveVotes[sessionId] = vote;
        return vote;
    }

    private static async Task<ulong> RunInternalAsync(
        uint sessionId,
        RunState runState,
        IReadOnlyList<GalleryDrawingSubmission> submissions)
    {
        List<GalleryDrawingSubmission> candidates = submissions
            .Where(submission => submission.PngBytes.Length > 0 && submission.CardIds.Count > 0)
            .OrderBy(submission => submission.OwnerId)
            .ToList();
        if (candidates.Count == 0)
        {
            return 0ul;
        }
        if (candidates.Count == 1)
        {
            return candidates[0].OwnerId;
        }

        Player localPlayer = LocalContext.GetMe(runState) ??
                             throw new InvalidOperationException("Could not find the local gallery voter.");
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            throw new InvalidOperationException("Could not open the gallery voting screen.");
        }

        GalleryDrawingVoteScreen screen = new();
        tree.Root.AddChild(screen);
        screen.Build(candidates);
        ulong selectedOwnerId = await screen._selection.Task;
        DrawingNetSync.PublishGalleryVote(sessionId, localPlayer.NetId, selectedOwnerId);
        screen.ShowWaiting();

        ulong[] votes = await Task.WhenAll(runState.Players
            .OrderBy(player => player.NetId)
            .Select(player => DrawingNetSync.WaitForGalleryVoteAsync(sessionId, player.NetId)));
        ulong winner = GalleryVoteResolver.Resolve(
            candidates.Select(candidate => candidate.OwnerId).ToList(),
            votes);
        screen.QueueFree();
        return winner;
    }

    private void Build(IReadOnlyList<GalleryDrawingSubmission> candidates)
    {
        Name = "DrawAndGuessMod_GalleryDrawingVoteScreen";
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 4100;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        ColorRect backdrop = new()
        {
            Color = new Color(0.025f, 0.02f, 0.035f, 0.94f),
            MouseFilter = MouseFilterEnum.Stop
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        MarginContainer margin = new();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 72);
        margin.AddThemeConstantOverride("margin_right", 72);
        margin.AddThemeConstantOverride("margin_top", 48);
        margin.AddThemeConstantOverride("margin_bottom", 48);
        AddChild(margin);

        VBoxContainer column = new() { Alignment = BoxContainer.AlignmentMode.Center };
        column.AddThemeConstantOverride("separation", 24);
        margin.AddChild(column);

        Label title = new()
        {
            Text = ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.VOTE_TITLE"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 34);
        column.AddChild(title);

        GridContainer grid = new() { Columns = Math.Min(4, candidates.Count) };
        grid.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        grid.AddThemeConstantOverride("h_separation", 24);
        grid.AddThemeConstantOverride("v_separation", 24);
        column.AddChild(grid);

        for (int index = 0; index < candidates.Count; index++)
        {
            GalleryDrawingSubmission candidate = candidates[index];
            VBoxContainer entry = new();
            entry.AddThemeConstantOverride("separation", 8);
            grid.AddChild(entry);

            Image image = new();
            if (image.LoadPngFromBuffer(candidate.PngBytes) != Error.Ok)
            {
                continue;
            }
            TextureButton button = new()
            {
                TextureNormal = ImageTexture.CreateFromImage(image),
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(250f, 340f),
                TooltipText = ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.VOTE_TOOLTIP")
            };
            ulong ownerId = candidate.OwnerId;
            button.Pressed += () => Select(ownerId);
            _buttons.Add(button);
            entry.AddChild(button);

            Label number = new()
            {
                Text = ModText.Format(
                    "DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.CANDIDATE",
                    ("Number", index + 1)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            number.AddThemeFontSizeOverride("font_size", 22);
            entry.AddChild(number);
        }

        _status = new Label
        {
            Text = ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.VOTE_HELP"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _status.AddThemeFontSizeOverride("font_size", 22);
        column.AddChild(_status);
    }

    private void Select(ulong ownerId)
    {
        if (_selection.TrySetResult(ownerId))
        {
            foreach (BaseButton button in _buttons)
            {
                button.Disabled = true;
            }
        }
    }

    private void ShowWaiting()
    {
        _status.Text = ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.WAITING_FOR_VOTES");
    }
}
