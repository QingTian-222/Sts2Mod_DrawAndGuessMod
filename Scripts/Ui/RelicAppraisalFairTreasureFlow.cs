using System.Reflection;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.State;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Ui;

internal static class RelicAppraisalFairTreasureFlow
{
    private const string TreasureRoomScenePath =
        "res://scenes/rooms/treasure_room.tscn";
    private static readonly Dictionary<uint, Task<IReadOnlyDictionary<ulong, string>>> ActiveFlows = new();
    private static readonly FieldInfo CurrentRelicsField = GetField("_currentRelics");
    private static readonly FieldInfo VotesField = GetField("_votes");
    private static readonly FieldInfo PredictedVoteField = GetField("_predictedVote");
    private static readonly FieldInfo SingleplayerSkippedField = GetField("_singleplayerSkipped");

    public static void Reset()
    {
        ActiveFlows.Clear();
    }

    public static Task<IReadOnlyDictionary<ulong, string>> RunAsync(
        uint appraisalId,
        RunState runState,
        IReadOnlyList<RelicAppraisalFairSubmission> submissions)
    {
        if (ActiveFlows.TryGetValue(appraisalId, out Task<IReadOnlyDictionary<ulong, string>>? active))
        {
            return active;
        }

        Task<IReadOnlyDictionary<ulong, string>> flow =
            RunInternalAsync(runState, submissions);
        ActiveFlows[appraisalId] = flow;
        return flow;
    }

    private static async Task<IReadOnlyDictionary<ulong, string>> RunInternalAsync(
        RunState runState,
        IReadOnlyList<RelicAppraisalFairSubmission> submissions)
    {
        List<RelicAppraisalFairSubmission> orderedSubmissions = submissions
            .OrderBy(submission => submission.OwnerId)
            .ToList();
        List<RelicModel> relics = orderedSubmissions
            .Select(submission => FindRelic(submission.TargetRelicId))
            .ToList();
        RelicAppraisalFairArtworkStore.InstallPresentations(orderedSubmissions);

        TreasureRoomRelicSynchronizer synchronizer =
            RunManager.Instance.TreasureRoomRelicSynchronizer;
        TaskCompletionSource<IReadOnlyDictionary<ulong, string>> resultCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRelicsAwarded(List<RelicPickingResult> results)
        {
            foreach (RelicPickingResult result in results)
            {
                if (result.player != null &&
                    result.type != RelicPickingResultType.Skipped)
                {
                    RelicAppraisalFairArtworkStore.MarkAwarded(result.relic);
                }
            }

            Dictionary<ulong, string> awards = results
                .Where(result => result.player != null &&
                                 result.type != RelicPickingResultType.Skipped)
                .ToDictionary(
                    result => result.player!.NetId,
                    result => result.relic.Id.Entry);
            resultCompletion.TrySetResult(awards);
        }

        synchronizer.RelicsAwarded += OnRelicsAwarded;
        Control? overlay = null;
        NTreasureRoomRelicCollection? collection = null;
        bool synchronizerPrepared = false;
        try
        {
            await PreloadManager.LoadRoomTreasureAssets(runState.Act);
            PrepareSynchronizer(synchronizer, runState, relics);
            synchronizerPrepared = true;
            PackedScene scene = ResourceLoader.Load<PackedScene>(
                TreasureRoomScenePath,
                null,
                ResourceLoader.CacheMode.Reuse);
            Node temporaryRoom = scene.Instantiate();
            collection = temporaryRoom.GetNode<NTreasureRoomRelicCollection>(
                "RelicCollection");
            Control fightBackstop = temporaryRoom.GetNode<Control>(
                "FightBackstop");
            Control handsContainer = temporaryRoom.GetNode<Control>(
                "HandsContainer");
            List<Node> sceneOwnedNodes = new();
            CollectSceneOwnedNodes(collection, temporaryRoom, sceneOwnedNodes);
            CollectSceneOwnedNodes(fightBackstop, temporaryRoom, sceneOwnedNodes);
            CollectSceneOwnedNodes(handsContainer, temporaryRoom, sceneOwnedNodes);
            temporaryRoom.RemoveChild(collection);
            temporaryRoom.RemoveChild(fightBackstop);
            temporaryRoom.RemoveChild(handsContainer);

            overlay = new Control
            {
                Name = "DrawAndGuessMod_RelicAppraisalFairTreasureOverlay",
                MouseFilter = Control.MouseFilterEnum.Stop,
                ZIndex = 0
            };
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

            ColorRect backstop = new()
            {
                Color = new Color(0.01f, 0.015f, 0.025f, 0.86f),
                MouseFilter = Control.MouseFilterEnum.Stop,
                ShowBehindParent = true
            };
            backstop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            overlay.AddChild(backstop);
            overlay.AddChild(collection);
            overlay.AddChild(fightBackstop);
            overlay.AddChild(handsContainer);
            foreach (Node sceneOwnedNode in sceneOwnedNodes)
            {
                sceneOwnedNode.Owner = overlay;
            }
            temporaryRoom.Free();

            Node parent = NRun.Instance != null
                ? NRun.Instance
                : ((SceneTree)Engine.GetMainLoop()).Root;
            parent.AddChild(overlay);

            collection.Initialize(runState);
            RelicAppraisalFairArtworkStore.SetPickingActive(true);
            collection.InitializeRelics();
            PlayCollectionAnimation(collection, nameof(collection.AnimIn), backstop);
            collection.DefaultFocusedControl?.TryGrabFocus();

            await collection.RelicPickingFinished();
            IReadOnlyDictionary<ulong, string> awards =
                await resultCompletion.Task;
            PlayCollectionAnimation(collection, nameof(collection.AnimOut), backstop);
            await collection.ToSignal(
                collection.GetTree().CreateTimer(0.35d, processAlways: true),
                SceneTreeTimer.SignalName.Timeout);
            return awards;
        }
        finally
        {
            RelicAppraisalFairArtworkStore.SetPickingActive(false);
            synchronizer.RelicsAwarded -= OnRelicsAwarded;
            if (synchronizerPrepared && synchronizer.CurrentRelics != null)
            {
                ResetSynchronizer(synchronizer);
            }
            if (overlay != null && GodotObject.IsInstanceValid(overlay))
            {
                overlay.QueueFree();
            }
            else if (collection != null && GodotObject.IsInstanceValid(collection))
            {
                collection.QueueFree();
            }
        }
    }

    private static void CollectSceneOwnedNodes(Node node, Node owner, List<Node> result)
    {
        if (node.Owner == owner)
        {
            result.Add(node);
        }
        foreach (Node child in node.GetChildren())
        {
            CollectSceneOwnedNodes(child, owner, result);
        }
    }

    private static void PlayCollectionAnimation(
        NTreasureRoomRelicCollection collection,
        string methodName,
        Node chestVisual)
    {
        MethodInfo? withChest = typeof(NTreasureRoomRelicCollection).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(Node)],
            null);
        if (withChest != null)
        {
            withChest.Invoke(collection, [chestVisual]);
            return;
        }

        MethodInfo? withoutChest = typeof(NTreasureRoomRelicCollection).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            null,
            Type.EmptyTypes,
            null);
        if (withoutChest == null)
        {
            throw new MissingMethodException(
                typeof(NTreasureRoomRelicCollection).FullName,
                methodName);
        }

        withoutChest.Invoke(collection, null);
    }

    private static void PrepareSynchronizer(
        TreasureRoomRelicSynchronizer synchronizer,
        RunState runState,
        List<RelicModel> relics)
    {
        if (synchronizer.CurrentRelics != null)
        {
            throw new InvalidOperationException(
                "Cannot start the relic appraisal fair while another relic picking session is active.");
        }

        CurrentRelicsField.SetValue(synchronizer, relics);
        List<TreasureRoomRelicSynchronizer.PlayerVote> votes =
            (List<TreasureRoomRelicSynchronizer.PlayerVote>)VotesField.GetValue(synchronizer)!;
        votes.Clear();
        foreach (var _ in runState.Players)
        {
            votes.Add(new TreasureRoomRelicSynchronizer.PlayerVote
            {
                voteReceived = false
            });
        }
        PredictedVoteField.SetValue(synchronizer, null);
        SingleplayerSkippedField.SetValue(synchronizer, false);
    }

    private static void ResetSynchronizer(
        TreasureRoomRelicSynchronizer synchronizer)
    {
        CurrentRelicsField.SetValue(synchronizer, null);
        ((List<TreasureRoomRelicSynchronizer.PlayerVote>)
            VotesField.GetValue(synchronizer)!).Clear();
        PredictedVoteField.SetValue(synchronizer, null);
        SingleplayerSkippedField.SetValue(synchronizer, false);
    }

    private static RelicModel FindRelic(string relicId)
    {
        return ModelDb.AllRelics.FirstOrDefault(relic =>
                   string.Equals(relic.Id.Entry, relicId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"The relic appraisal fair references missing relic '{relicId}'.");
    }

    private static FieldInfo GetField(string name)
    {
        return typeof(TreasureRoomRelicSynchronizer).GetField(
                   name,
                   BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingFieldException(
                   typeof(TreasureRoomRelicSynchronizer).FullName,
                   name);
    }
}
