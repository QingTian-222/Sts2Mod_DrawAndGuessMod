using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Networking;

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
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), DrawingFinalMessage> PendingFinals = new();

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
        PendingFinals.Clear();
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
            _registeredBuffer.UnregisterMessageHandler<DrawingFinalMessage>(OnFinalReceived);
        }

        current.RegisterMessageHandler<DrawingChallengeTargetMessage>(OnChallengeTargetReceived);
        current.RegisterMessageHandler<DrawingSyncMessage>(OnCommandsReceived);
        current.RegisterMessageHandler<DrawingUndoRequestMessage>(OnUndoRequestReceived);
        current.RegisterMessageHandler<DrawingRedoRequestMessage>(OnRedoRequestReceived);
        current.RegisterMessageHandler<DrawingCanvasStateMessage>(OnCanvasStateReceived);
        current.RegisterMessageHandler<DrawingTimerSyncMessage>(OnTimerReceived);
        current.RegisterMessageHandler<DrawingFinalMessage>(OnFinalReceived);
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
}
