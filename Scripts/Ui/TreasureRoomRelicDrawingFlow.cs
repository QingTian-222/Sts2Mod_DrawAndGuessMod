using System.Reflection;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.State;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Ui;

internal static class TreasureRoomRelicDrawingFlow
{
    private static readonly MethodInfo OpenChestMethod = AccessTools.Method(
        typeof(NTreasureRoom),
        "OpenChest") ?? throw new MissingMethodException(typeof(NTreasureRoom).FullName, "OpenChest");
    private static readonly HashSet<ulong> ActiveRoomInstances = new();

    public static bool ShouldRun(RunState runState)
    {
        return DrawingNetSync.IsMultiplayer &&
               runState.Players.Count > 1 &&
               DrawingRunRules.IsGameplayEnabled(runState) &&
               DrawingRunRules.GetTreasureRoomRelicDrawingEnabled(runState);
    }

    public static void Start(NTreasureRoom room, RunState runState)
    {
        ulong instanceId = room.GetInstanceId();
        if (!ActiveRoomInstances.Add(instanceId))
        {
            return;
        }

        NButton? chestButton = room.GetNodeOrNull<NButton>("%Chest");
        chestButton?.Disable();
        _ = RunSafelyAsync(room, runState, instanceId, chestButton);
    }

    private static async Task RunSafelyAsync(NTreasureRoom room, RunState runState, ulong instanceId, NButton? chestButton)
    {
        try
        {
            await RunAsync(room, runState);
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[DrawAndGuessMod] Treasure-room relic drawing failed: {ex}");
            if (GodotObject.IsInstanceValid(room) &&
                chestButton != null &&
                GodotObject.IsInstanceValid(chestButton))
            {
                chestButton.Enable();
            }
        }
        finally
        {
            ActiveRoomInstances.Remove(instanceId);
        }
    }

    private static async Task RunAsync(NTreasureRoom room, RunState runState)
    {
        IReadOnlyList<RelicModel>? currentRelics = null;
        for (int frame = 0; frame < 300 && currentRelics == null; frame++)
        {
            if (!GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
            {
                return;
            }

            currentRelics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
            if (currentRelics == null)
            {
                await room.ToSignal(room.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }

        if (currentRelics == null)
        {
            throw new TimeoutException("The vanilla treasure relic candidates were not generated in time.");
        }

        List<RelicModel> targets = currentRelics.ToList();
        if (targets.Count == 0)
        {
            await InvokeOpenChestAsync(room);
            return;
        }

        List<Player> participants = runState.Players
            .Where(player => Hook.ShouldGenerateTreasure(runState, player))
            .ToList();
        if (participants.Count != targets.Count)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Treasure drawing participant count {participants.Count} did not match " +
                $"the vanilla candidate count {targets.Count}; falling back to run player order.");
            participants = runState.Players.Take(targets.Count).ToList();
        }

        uint appraisalId = CreateSessionId(runState);
        if (DrawingNetSync.IsLocalHost)
        {
            DrawingNetSync.BeginRelicAppraisalFair();
            for (int index = 0; index < participants.Count; index++)
            {
                DrawingNetSync.PublishAppraisalTarget(
                    appraisalId,
                    participants[index].NetId,
                    targets[index].Id.Entry);
            }
        }

        Player localPlayer = LocalContext.GetMe(runState)
            ?? throw new InvalidOperationException("The local treasure-room player was not available.");
        Control? nonParticipantWaitScreen = null;
        if (participants.Any(player => player.NetId == localPlayer.NetId))
        {
            await DrawAndPublishLocalSubmissionAsync(appraisalId, localPlayer);
        }
        else
        {
            nonParticipantWaitScreen = CreateWaitingScreen();
        }

        RelicAppraisalFairSubmission[] submissions;
        try
        {
            submissions = await Task.WhenAll(participants.Select(player =>
                DrawingNetSync.WaitForAppraisalSubmissionAsync(appraisalId, player.NetId)));
        }
        finally
        {
            DrawingScreen.CloseCompletedRelicScreen();
            if (nonParticipantWaitScreen != null &&
                GodotObject.IsInstanceValid(nonParticipantWaitScreen))
            {
                nonParticipantWaitScreen.QueueFree();
            }
        }

        ValidateSubmissions(participants, targets, submissions);
        RelicAppraisalFairArtworkStore.InstallPresentations(submissions);
        TreasureRoomRelicSynchronizer synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
        void OnRelicsAwarded(List<RelicPickingResult> results)
        {
            foreach (RelicPickingResult result in results)
            {
                if (result.player != null && result.type != RelicPickingResultType.Skipped)
                {
                    RelicAppraisalFairArtworkStore.MarkAwarded(result.relic);
                }
            }
        }

        synchronizer.RelicsAwarded += OnRelicsAwarded;
        RelicAppraisalFairArtworkStore.SetPickingActive(true);
        try
        {
            await InvokeOpenChestAsync(room);
        }
        finally
        {
            RelicAppraisalFairArtworkStore.SetPickingActive(false);
            synchronizer.RelicsAwarded -= OnRelicsAwarded;
        }
    }

    private static async Task DrawAndPublishLocalSubmissionAsync(uint appraisalId, Player localPlayer)
    {
        string targetRelicId = await DrawingNetSync.WaitForAppraisalTargetAsync(
            appraisalId,
            localPlayer.NetId);
        RelicModel target = FindRelic(targetRelicId);
        DrawingScreenOptions options = new(
            ModText.Get(
                $"目标：{target.Title.GetFormattedText()}",
                $"Target: {target.Title.GetFormattedText()}"),
            ModText.Get(
                "绘画目标遗物，达到匹配度即可提交；只有你知道它原本是什么。",
                "Draw the target relic and reach the required match to submit; only you know what it originally was."),
            PeekTooltip: ModText.Get("查看宝箱房", "View treasure room"),
            InitialCanvasMode: DrawingCanvasMode.Relic,
            AllowCanvasModeSwitch: false);
        RelicDrawingResult? drawing = null;
        try
        {
            drawing = await DrawingScreen.ShowRelicAsync(
                localPlayer,
                appraisalId ^ (uint)localPlayer.NetId,
                target,
                options);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Relic drawing UI failed for {localPlayer.NetId}; " +
                $"submitting the original art as a fallback: {ex.Message}");
        }
        RelicAppraisalFairSubmission submission = drawing == null
            ? CreateFallbackSubmission(localPlayer, target)
            : new RelicAppraisalFairSubmission(
                localPlayer.NetId,
                target.Id.Entry,
                drawing.WorkTitle,
                drawing.PngBytes,
                drawing.SkippedCreation);
        DrawingNetSync.PublishAppraisalSubmission(appraisalId, submission);
        try
        {
            if (!submission.UseOriginalPresentation)
            {
                DrawingHistoryStore.RecordRelic(
                    localPlayer.RunState,
                    localPlayer.NetId,
                    appraisalId ^ (uint)localPlayer.NetId,
                    target,
                    submission.WorkTitle,
                    submission.PngBytes);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to record treasure-room relic artwork: {ex.Message}");
        }
    }

    private static RelicAppraisalFairSubmission CreateFallbackSubmission(Player player, RelicModel target)
    {
        Image source = target.BigIcon.GetImage();
        if (source.IsEmpty())
        {
            throw new InvalidOperationException($"Could not export fallback artwork for {target.Id.Entry}.");
        }

        return new RelicAppraisalFairSubmission(
            player.NetId,
            target.Id.Entry,
            target.Title.GetFormattedText(),
            source.SavePngToBuffer(),
            UseOriginalPresentation: true);
    }

    private static void ValidateSubmissions(IReadOnlyList<Player> participants, IReadOnlyList<RelicModel> targets, IReadOnlyList<RelicAppraisalFairSubmission> submissions)
    {
        if (participants.Count != targets.Count || submissions.Count != targets.Count)
        {
            throw new InvalidOperationException("Treasure-room relic drawing result counts do not match.");
        }

        for (int index = 0; index < submissions.Count; index++)
        {
            RelicAppraisalFairSubmission submission = submissions[index];
            if (submission.OwnerId != participants[index].NetId ||
                !string.Equals(
                    submission.TargetRelicId,
                    targets[index].Id.Entry,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Treasure-room relic drawing target mismatch for player {participants[index].NetId}.");
            }

            Image image = new();
            if (image.LoadPngFromBuffer(submission.PngBytes) != Error.Ok)
            {
                throw new InvalidOperationException(
                    $"Treasure-room relic drawing from {submission.OwnerId} contains an invalid PNG.");
            }
        }
    }

    private static Control CreateWaitingScreen()
    {
        Control screen = new()
        {
            Name = "DrawAndGuessMod_TreasureRelicWaiting",
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 100
        };
        screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        ColorRect backstop = new()
        {
            Color = new Color(0f, 0f, 0f, 0.72f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        backstop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(backstop);
        Label label = new()
        {
            Text = ModText.Get("等待其他玩家", "Waiting for other players"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeFontSizeOverride("font_size", 32);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(label);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(screen);
        return screen;
    }

    private static async Task InvokeOpenChestAsync(NTreasureRoom room)
    {
        if (!GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
        {
            return;
        }

        if (OpenChestMethod.Invoke(room, null) is not Task openTask)
        {
            throw new InvalidOperationException("NTreasureRoom.OpenChest did not return a Task.");
        }
        await openTask;
    }

    private static RelicModel FindRelic(string relicId)
    {
        return ModelDb.AllRelics.FirstOrDefault(relic =>
                   string.Equals(relic.Id.Entry, relicId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"Treasure-room drawing references missing relic '{relicId}'.");
    }

    private static uint CreateSessionId(RunState runState)
    {
        ulong hostId = DrawingNetSync.HostNetId;
        uint hostHash = (uint)(hostId ^ hostId >> 32);
        return 0xC7000000u ^
               hostHash ^
               unchecked((uint)runState.TotalFloor * 0x9E3779B9u) ^
               unchecked((uint)(runState.CurrentActIndex + 1) * 0x85EBCA6Bu);
    }
}
