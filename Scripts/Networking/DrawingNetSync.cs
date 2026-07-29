using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Networking;

internal sealed record RelicAuctionSubmission(
    ulong OwnerId,
    string TargetRelicId,
    string WorkTitle,
    byte[] PngBytes);

internal static class DrawingNetSync
{
    private static RunLocationTargetedMessageBuffer? _registeredBuffer;
    private static uint _galleryEventEpoch;
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), string> ChallengeTargets = new();
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), TaskCompletionSource<string>> ChallengeTargetWaiters = new();
    private static readonly Dictionary<uint, string> CardSelections = new();
    private static readonly Dictionary<uint, TaskCompletionSource<string>> CardSelectionWaiters = new();
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), List<(DrawingSyncMessage Message, ulong SenderId)>> PendingBatches = new();
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), DrawingCanvasStateMessage> PendingCanvasStates = new();
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), DrawingTimerSyncMessage> PendingTimers = new();
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), DrawingBlankSettingsMessage> PendingBlankSettings = new();
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), DrawingFinalMessage> PendingFinals = new();
    private static readonly Dictionary<(uint AuctionId, ulong OwnerId), string> AuctionTargets = new();
    private static readonly Dictionary<(uint AuctionId, ulong OwnerId), TaskCompletionSource<string>> AuctionTargetWaiters = new();
    private static readonly Dictionary<(uint AuctionId, ulong OwnerId), RelicAuctionSubmission> AuctionSubmissions = new();
    private static readonly Dictionary<(uint AuctionId, ulong OwnerId), TaskCompletionSource<RelicAuctionSubmission>> AuctionSubmissionWaiters = new();
    private static readonly Dictionary<(uint AuctionId, ulong VoterId), ulong> AuctionVotes = new();
    private static readonly Dictionary<(uint AuctionId, ulong VoterId), TaskCompletionSource<ulong>> AuctionVoteWaiters = new();
    private static readonly Dictionary<uint, IReadOnlyDictionary<ulong, string>> AuctionResults = new();
    private static readonly Dictionary<uint, TaskCompletionSource<IReadOnlyDictionary<ulong, string>>> AuctionResultWaiters = new();

    public static bool IsMultiplayer
    {
        get
        {
            INetGameService? netService = RunManager.Instance.NetService;
            return netService != null && netService.Type.IsMultiplayer();
        }
    }

    public static bool IsLocalHost =>
        RunManager.Instance.NetService.Type is NetGameType.Host or NetGameType.Singleplayer;

    public static uint GalleryEventEpoch => _galleryEventEpoch;

    public static ulong HostNetId
    {
        get
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (netService.Type is NetGameType.Host or NetGameType.Singleplayer)
            {
                return netService.NetId;
            }
            if (netService is INetClientGameService client && client.NetClient != null)
            {
                return client.NetClient.HostNetId;
            }
            throw new InvalidOperationException("Could not determine the multiplayer host.");
        }
    }

    public static void Reset()
    {
        _galleryEventEpoch = 0u;
        ClearGalleryState();
        PendingBatches.Clear();
        PendingCanvasStates.Clear();
        PendingTimers.Clear();
        PendingBlankSettings.Clear();
        PendingFinals.Clear();
        ClearRelicAuctionState();
        EnsureRegistered();
    }

    public static void BeginGalleryEvent()
    {
        _galleryEventEpoch++;
        if (_galleryEventEpoch == 0u)
        {
            _galleryEventEpoch = 1u;
        }
        ClearGalleryState();
        EnsureRegistered();
    }

    private static void ClearGalleryState()
    {
        foreach (TaskCompletionSource<string> waiter in ChallengeTargetWaiters.Values)
        {
            waiter.TrySetCanceled();
        }
        ChallengeTargets.Clear();
        ChallengeTargetWaiters.Clear();
        foreach (TaskCompletionSource<string> waiter in CardSelectionWaiters.Values)
        {
            waiter.TrySetCanceled();
        }
        CardSelections.Clear();
        CardSelectionWaiters.Clear();
    }

    public static void BeginRelicAuction()
    {
        ClearRelicAuctionState();
        EnsureRegistered();
    }

    private static void ClearRelicAuctionState()
    {
        foreach (TaskCompletionSource<string> waiter in AuctionTargetWaiters.Values)
        {
            waiter.TrySetCanceled();
        }
        foreach (TaskCompletionSource<RelicAuctionSubmission> waiter in AuctionSubmissionWaiters.Values)
        {
            waiter.TrySetCanceled();
        }
        foreach (TaskCompletionSource<ulong> waiter in AuctionVoteWaiters.Values)
        {
            waiter.TrySetCanceled();
        }
        foreach (TaskCompletionSource<IReadOnlyDictionary<ulong, string>> waiter in AuctionResultWaiters.Values)
        {
            waiter.TrySetCanceled();
        }

        AuctionTargets.Clear();
        AuctionTargetWaiters.Clear();
        AuctionSubmissions.Clear();
        AuctionSubmissionWaiters.Clear();
        AuctionVotes.Clear();
        AuctionVoteWaiters.Clear();
        AuctionResults.Clear();
        AuctionResultWaiters.Clear();
    }

    public static void PublishAuctionTarget(uint auctionId, ulong ownerId, string targetRelicId)
    {
        EnsureRegistered();
        AcceptAuctionTarget(auctionId, ownerId, targetRelicId);
        if (!IsMultiplayer)
        {
            return;
        }

        RunManager.Instance.NetService.SendMessage(new RelicAuctionTargetMessage
        {
            AuctionId = auctionId,
            OwnerId = ownerId,
            TargetRelicId = targetRelicId,
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static Task<string> WaitForAuctionTargetAsync(uint auctionId, ulong ownerId)
    {
        EnsureRegistered();
        (uint AuctionId, ulong OwnerId) key = (auctionId, ownerId);
        if (AuctionTargets.TryGetValue(key, out string? target))
        {
            return Task.FromResult(target);
        }

        if (!AuctionTargetWaiters.TryGetValue(key, out TaskCompletionSource<string>? waiter))
        {
            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            AuctionTargetWaiters[key] = waiter;
        }
        return waiter.Task;
    }

    public static void PublishAuctionSubmission(uint auctionId, RelicAuctionSubmission submission)
    {
        EnsureRegistered();
        AcceptAuctionSubmission(auctionId, submission);
        if (!IsMultiplayer)
        {
            return;
        }

        RunManager.Instance.NetService.SendMessage(new RelicAuctionSubmissionMessage
        {
            AuctionId = auctionId,
            OwnerId = submission.OwnerId,
            TargetRelicId = submission.TargetRelicId,
            WorkTitle = submission.WorkTitle,
            PngBytes = submission.PngBytes,
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static Task<RelicAuctionSubmission> WaitForAuctionSubmissionAsync(uint auctionId, ulong ownerId)
    {
        EnsureRegistered();
        (uint AuctionId, ulong OwnerId) key = (auctionId, ownerId);
        if (AuctionSubmissions.TryGetValue(key, out RelicAuctionSubmission? submission))
        {
            return Task.FromResult(submission);
        }

        if (!AuctionSubmissionWaiters.TryGetValue(key, out TaskCompletionSource<RelicAuctionSubmission>? waiter))
        {
            waiter = new TaskCompletionSource<RelicAuctionSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
            AuctionSubmissionWaiters[key] = waiter;
        }
        return waiter.Task;
    }

    public static void PublishAuctionVote(uint auctionId, ulong voterId, ulong workOwnerId)
    {
        EnsureRegistered();
        AcceptAuctionVote(auctionId, voterId, workOwnerId);
        if (!IsMultiplayer)
        {
            return;
        }

        RunManager.Instance.NetService.SendMessage(new RelicAuctionVoteMessage
        {
            AuctionId = auctionId,
            VoterId = voterId,
            WorkOwnerId = workOwnerId,
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static Task<ulong> WaitForAuctionVoteAsync(uint auctionId, ulong voterId)
    {
        EnsureRegistered();
        (uint AuctionId, ulong VoterId) key = (auctionId, voterId);
        if (AuctionVotes.TryGetValue(key, out ulong workOwnerId))
        {
            return Task.FromResult(workOwnerId);
        }

        if (!AuctionVoteWaiters.TryGetValue(key, out TaskCompletionSource<ulong>? waiter))
        {
            waiter = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
            AuctionVoteWaiters[key] = waiter;
        }
        return waiter.Task;
    }

    public static void PublishAuctionResults(uint auctionId, IReadOnlyDictionary<ulong, string> awardedRelicIds)
    {
        EnsureRegistered();
        AcceptAuctionResults(auctionId, awardedRelicIds);
        if (!IsMultiplayer)
        {
            return;
        }

        RunManager.Instance.NetService.SendMessage(new RelicAuctionResultMessage
        {
            AuctionId = auctionId,
            AwardedRelicIds = new Dictionary<ulong, string>(awardedRelicIds),
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static Task<IReadOnlyDictionary<ulong, string>> WaitForAuctionResultsAsync(uint auctionId)
    {
        EnsureRegistered();
        if (AuctionResults.TryGetValue(auctionId, out IReadOnlyDictionary<ulong, string>? results))
        {
            return Task.FromResult(results);
        }

        if (!AuctionResultWaiters.TryGetValue(
                auctionId,
                out TaskCompletionSource<IReadOnlyDictionary<ulong, string>>? waiter))
        {
            waiter = new TaskCompletionSource<IReadOnlyDictionary<ulong, string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            AuctionResultWaiters[auctionId] = waiter;
        }
        return waiter.Task;
    }

    public static void BeginSession(ulong ownerId, uint sessionId)
    {
        EnsureRegistered();
    }

    public static void PublishChallengeTarget(ulong ownerId, uint sessionId, string targetCardId)
    {
        EnsureRegistered();
        AcceptChallengeTarget(ownerId, sessionId, targetCardId);
        if (!IsMultiplayer)
        {
            return;
        }

        RunManager.Instance.NetService.SendMessage(new DrawingChallengeTargetMessage
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            TargetCardId = targetCardId,
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static Task<string> WaitForChallengeTargetAsync(ulong ownerId, uint sessionId)
    {
        EnsureRegistered();
        (ulong OwnerId, uint SessionId) key = (ownerId, sessionId);
        if (ChallengeTargets.TryGetValue(key, out string? targetCardId))
        {
            return Task.FromResult(targetCardId);
        }

        if (!ChallengeTargetWaiters.TryGetValue(key, out TaskCompletionSource<string>? waiter))
        {
            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChallengeTargetWaiters[key] = waiter;
        }
        return waiter.Task;
    }

    public static void CompleteCardSelection(uint sessionId, string selectedCardId)
    {
        EnsureRegistered();
        AcceptCardSelection(sessionId, selectedCardId);
    }

    public static Task<string> WaitForCardSelectionAsync(uint sessionId)
    {
        EnsureRegistered();
        if (CardSelections.TryGetValue(sessionId, out string? selectedCardId))
        {
            return Task.FromResult(selectedCardId);
        }

        if (!CardSelectionWaiters.TryGetValue(sessionId, out TaskCompletionSource<string>? waiter))
        {
            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CardSelectionWaiters[sessionId] = waiter;
        }
        return waiter.Task;
    }

    public static void SendCommands(ulong ownerId, uint sessionId, uint epoch, IReadOnlyList<DrawingCommand> commands)
    {
        if (!IsMultiplayer || commands.Count == 0)
        {
            return;
        }

        EnsureRegistered();
        DrawingSyncMessage message = new()
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            Epoch = epoch,
            Commands = new List<DrawingCommand>(commands),
            LocationValue = _registeredBuffer!.CurrentLocation
        };
        RunManager.Instance.NetService.SendMessage(message);
    }

    public static void SendUndoRequest(ulong ownerId, uint sessionId)
    {
        if (!IsMultiplayer)
        {
            return;
        }

        EnsureRegistered();
        RunManager.Instance.NetService.SendMessage(new DrawingUndoRequestMessage
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static void SendRedoRequest(ulong ownerId, uint sessionId)
    {
        if (!IsMultiplayer)
        {
            return;
        }

        EnsureRegistered();
        RunManager.Instance.NetService.SendMessage(new DrawingRedoRequestMessage
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static void SendCanvasState(DrawingCanvasStateMessage message)
    {
        if (!IsMultiplayer)
        {
            return;
        }

        EnsureRegistered();
        message.LocationValue = _registeredBuffer!.CurrentLocation;
        RunManager.Instance.NetService.SendMessage(message);
    }

    public static void SendFinal(DrawingFinalMessage message)
    {
        if (!IsMultiplayer)
        {
            return;
        }

        EnsureRegistered();
        message.LocationValue = _registeredBuffer!.CurrentLocation;
        RunManager.Instance.NetService.SendMessage(message);
    }

    public static void SendTimer(ulong ownerId, uint sessionId, double remainingSeconds)
    {
        if (!IsMultiplayer)
        {
            return;
        }

        EnsureRegistered();
        RunManager.Instance.NetService.SendMessage(new DrawingTimerSyncMessage
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            RemainingMilliseconds = Math.Max(0, (int)Math.Ceiling(remainingSeconds * 1000d)),
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static void SendBlankSettings(ulong ownerId, uint sessionId, bool excludePreviouslySelectedCards)
    {
        if (!IsMultiplayer)
        {
            return;
        }

        EnsureRegistered();
        RunManager.Instance.NetService.SendMessage(new DrawingBlankSettingsMessage
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            ExcludePreviouslySelectedCards = excludePreviouslySelectedCards,
            LocationValue = _registeredBuffer!.CurrentLocation
        });
    }

    public static void DeliverPending(DrawingScreen screen, ulong ownerId, uint sessionId)
    {
        (ulong OwnerId, uint SessionId) key = (ownerId, sessionId);
        if (PendingCanvasStates.Remove(key, out DrawingCanvasStateMessage? canvasState))
        {
            screen.ReceiveCanvasState(canvasState);
        }

        if (PendingBatches.Remove(key, out List<(DrawingSyncMessage Message, ulong SenderId)>? batches))
        {
            foreach ((DrawingSyncMessage message, ulong senderId) in batches)
            {
                screen.ReceiveCommands(message, senderId);
            }
        }

        if (PendingTimers.Remove(key, out DrawingTimerSyncMessage? timer))
        {
            screen.ReceiveTimer(timer);
        }

        if (PendingBlankSettings.Remove(key, out DrawingBlankSettingsMessage? blankSettings))
        {
            screen.ReceiveBlankSettings(blankSettings);
        }

        if (PendingFinals.Remove(key, out DrawingFinalMessage? final))
        {
            screen.ReceiveFinal(final);
        }
    }

    private static void EnsureRegistered()
    {
        RunLocationTargetedMessageBuffer? current = RunManager.Instance.RunLocationTargetedBuffer;
        if (current == null || ReferenceEquals(current, _registeredBuffer))
        {
            return;
        }

        if (_registeredBuffer != null)
        {
            _registeredBuffer.UnregisterMessageHandler<DrawingChallengeTargetMessage>(OnChallengeTargetReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingSyncMessage>(OnCommandsReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingUndoRequestMessage>(OnUndoRequestReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingRedoRequestMessage>(OnRedoRequestReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingCanvasStateMessage>(OnCanvasStateReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingTimerSyncMessage>(OnTimerReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingBlankSettingsMessage>(OnBlankSettingsReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingFinalMessage>(OnFinalReceived);
            _registeredBuffer.UnregisterMessageHandler<RelicAuctionTargetMessage>(OnAuctionTargetReceived);
            _registeredBuffer.UnregisterMessageHandler<RelicAuctionSubmissionMessage>(OnAuctionSubmissionReceived);
            _registeredBuffer.UnregisterMessageHandler<RelicAuctionVoteMessage>(OnAuctionVoteReceived);
            _registeredBuffer.UnregisterMessageHandler<RelicAuctionResultMessage>(OnAuctionResultReceived);
        }

        current.RegisterMessageHandler<DrawingChallengeTargetMessage>(OnChallengeTargetReceived);
        current.RegisterMessageHandler<DrawingSyncMessage>(OnCommandsReceived);
        current.RegisterMessageHandler<DrawingUndoRequestMessage>(OnUndoRequestReceived);
        current.RegisterMessageHandler<DrawingRedoRequestMessage>(OnRedoRequestReceived);
        current.RegisterMessageHandler<DrawingCanvasStateMessage>(OnCanvasStateReceived);
        current.RegisterMessageHandler<DrawingTimerSyncMessage>(OnTimerReceived);
        current.RegisterMessageHandler<DrawingBlankSettingsMessage>(OnBlankSettingsReceived);
        current.RegisterMessageHandler<DrawingFinalMessage>(OnFinalReceived);
        current.RegisterMessageHandler<RelicAuctionTargetMessage>(OnAuctionTargetReceived);
        current.RegisterMessageHandler<RelicAuctionSubmissionMessage>(OnAuctionSubmissionReceived);
        current.RegisterMessageHandler<RelicAuctionVoteMessage>(OnAuctionVoteReceived);
        current.RegisterMessageHandler<RelicAuctionResultMessage>(OnAuctionResultReceived);
        _registeredBuffer = current;
    }

    private static void OnChallengeTargetReceived(DrawingChallengeTargetMessage message, ulong senderId)
    {
        if (senderId != message.OwnerId)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Rejected gallery target from {senderId}; expected owner {message.OwnerId}.");
            return;
        }

        AcceptChallengeTarget(message.OwnerId, message.SessionId, message.TargetCardId);
    }

    private static void AcceptChallengeTarget(ulong ownerId, uint sessionId, string targetCardId)
    {
        (ulong OwnerId, uint SessionId) key = (ownerId, sessionId);
        ChallengeTargets[key] = targetCardId;
        if (ChallengeTargetWaiters.Remove(key, out TaskCompletionSource<string>? waiter))
        {
            waiter.TrySetResult(targetCardId);
        }
    }

    private static void AcceptCardSelection(uint sessionId, string selectedCardId)
    {
        CardSelections[sessionId] = selectedCardId;
        if (CardSelectionWaiters.Remove(sessionId, out TaskCompletionSource<string>? waiter))
        {
            waiter.TrySetResult(selectedCardId);
        }
    }

    private static void OnCommandsReceived(DrawingSyncMessage message, ulong senderId)
    {
        if (senderId == RunManager.Instance.NetService.NetId)
        {
            return;
        }

        if (DrawingScreen.TryReceiveCommands(message, senderId))
        {
            return;
        }

        (ulong OwnerId, uint SessionId) key = (message.OwnerId, message.SessionId);
        if (!PendingBatches.TryGetValue(key, out List<(DrawingSyncMessage Message, ulong SenderId)>? batches))
        {
            batches = new List<(DrawingSyncMessage Message, ulong SenderId)>();
            PendingBatches[key] = batches;
        }
        batches.Add((message, senderId));
    }

    private static void OnUndoRequestReceived(DrawingUndoRequestMessage message, ulong senderId)
    {
        if (senderId == RunManager.Instance.NetService.NetId)
        {
            return;
        }

        DrawingScreen.TryReceiveUndoRequest(message, senderId);
    }

    private static void OnRedoRequestReceived(DrawingRedoRequestMessage message, ulong senderId)
    {
        if (senderId == RunManager.Instance.NetService.NetId)
        {
            return;
        }

        DrawingScreen.TryReceiveRedoRequest(message, senderId);
    }

    private static void OnCanvasStateReceived(DrawingCanvasStateMessage message, ulong senderId)
    {
        if (senderId != message.OwnerId)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Rejected authoritative canvas state from {senderId}; expected owner {message.OwnerId}.");
            return;
        }

        if (DrawingScreen.TryReceiveCanvasState(message))
        {
            return;
        }

        (ulong OwnerId, uint SessionId) key = (message.OwnerId, message.SessionId);
        if (!PendingCanvasStates.TryGetValue(key, out DrawingCanvasStateMessage? pending) ||
            message.Epoch > pending.Epoch ||
            message.Epoch == pending.Epoch && message.StateSequence >= pending.StateSequence)
        {
            PendingCanvasStates[key] = message;
        }
    }

    private static void OnTimerReceived(DrawingTimerSyncMessage message, ulong senderId)
    {
        if (senderId != HostNetId)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Rejected drawing timer from {senderId}; expected host {HostNetId}.");
            return;
        }

        if (DrawingScreen.TryReceiveTimer(message))
        {
            return;
        }

        (ulong OwnerId, uint SessionId) key = (message.OwnerId, message.SessionId);
        if (!PendingTimers.TryGetValue(key, out DrawingTimerSyncMessage? pending) ||
            message.RemainingMilliseconds < pending.RemainingMilliseconds)
        {
            PendingTimers[key] = message;
        }
    }

    private static void OnBlankSettingsReceived(DrawingBlankSettingsMessage message, ulong senderId)
    {
        if (senderId != HostNetId)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Rejected Blank settings from {senderId}; expected host {HostNetId}.");
            return;
        }

        if (DrawingScreen.TryReceiveBlankSettings(message))
        {
            return;
        }

        PendingBlankSettings[(message.OwnerId, message.SessionId)] = message;
    }

    private static void OnFinalReceived(DrawingFinalMessage message, ulong senderId)
    {
        if (senderId != message.OwnerId)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Rejected drawing final from {senderId}; expected owner {message.OwnerId}.");
            return;
        }

        if (!DrawingScreen.TryReceiveFinal(message))
        {
            PendingFinals[(message.OwnerId, message.SessionId)] = message;
        }
    }

    private static void OnAuctionTargetReceived(RelicAuctionTargetMessage message, ulong senderId)
    {
        if (senderId != HostNetId)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Rejected relic auction target from non-host {senderId}.");
            return;
        }
        AcceptAuctionTarget(message.AuctionId, message.OwnerId, message.TargetRelicId);
    }

    private static void AcceptAuctionTarget(uint auctionId, ulong ownerId, string targetRelicId)
    {
        (uint AuctionId, ulong OwnerId) key = (auctionId, ownerId);
        AuctionTargets[key] = targetRelicId;
        if (AuctionTargetWaiters.Remove(key, out TaskCompletionSource<string>? waiter))
        {
            waiter.TrySetResult(targetRelicId);
        }
    }

    private static void OnAuctionSubmissionReceived(RelicAuctionSubmissionMessage message, ulong senderId)
    {
        if (senderId != message.OwnerId)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Rejected relic auction submission from {senderId}; expected {message.OwnerId}.");
            return;
        }
        AcceptAuctionSubmission(
            message.AuctionId,
            new RelicAuctionSubmission(
                message.OwnerId,
                message.TargetRelicId,
                message.WorkTitle,
                message.PngBytes));
    }

    private static void AcceptAuctionSubmission(uint auctionId, RelicAuctionSubmission submission)
    {
        (uint AuctionId, ulong OwnerId) key = (auctionId, submission.OwnerId);
        AuctionSubmissions[key] = submission;
        if (AuctionSubmissionWaiters.Remove(
                key,
                out TaskCompletionSource<RelicAuctionSubmission>? waiter))
        {
            waiter.TrySetResult(submission);
        }
    }

    private static void OnAuctionVoteReceived(RelicAuctionVoteMessage message, ulong senderId)
    {
        if (senderId != message.VoterId)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Rejected relic auction vote from {senderId}; expected {message.VoterId}.");
            return;
        }
        AcceptAuctionVote(message.AuctionId, message.VoterId, message.WorkOwnerId);
    }

    private static void AcceptAuctionVote(uint auctionId, ulong voterId, ulong workOwnerId)
    {
        (uint AuctionId, ulong VoterId) key = (auctionId, voterId);
        AuctionVotes[key] = workOwnerId;
        if (AuctionVoteWaiters.Remove(key, out TaskCompletionSource<ulong>? waiter))
        {
            waiter.TrySetResult(workOwnerId);
        }
    }

    private static void OnAuctionResultReceived(RelicAuctionResultMessage message, ulong senderId)
    {
        if (senderId != HostNetId)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Rejected relic auction result from non-host {senderId}.");
            return;
        }
        AcceptAuctionResults(message.AuctionId, message.AwardedRelicIds);
    }

    private static void AcceptAuctionResults(uint auctionId, IReadOnlyDictionary<ulong, string> results)
    {
        Dictionary<ulong, string> copy = results.ToDictionary(pair => pair.Key, pair => pair.Value);
        AuctionResults[auctionId] = copy;
        if (AuctionResultWaiters.Remove(
                auctionId,
                out TaskCompletionSource<IReadOnlyDictionary<ulong, string>>? waiter))
        {
            waiter.TrySetResult(copy);
        }
    }
}
