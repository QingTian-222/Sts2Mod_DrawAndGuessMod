using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Networking;

internal static class DrawingNetSync
{
    private static RunLocationTargetedMessageBuffer? _registeredBuffer;
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), List<(DrawingSyncMessage Message, ulong SenderId)>> PendingBatches = new();
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), DrawingCanvasStateMessage> PendingCanvasStates = new();
    private static readonly Dictionary<(ulong OwnerId, uint SessionId), DrawingFinalMessage> PendingFinals = new();

    public static bool IsMultiplayer
    {
        get
        {
            INetGameService? netService = RunManager.Instance.NetService;
            return netService != null && netService.Type.IsMultiplayer();
        }
    }

    public static void Reset()
    {
        PendingBatches.Clear();
        PendingCanvasStates.Clear();
        PendingFinals.Clear();
        EnsureRegistered();
    }

    public static void BeginSession(ulong ownerId, uint sessionId)
    {
        EnsureRegistered();
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
            _registeredBuffer.UnregisterMessageHandler<DrawingSyncMessage>(OnCommandsReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingUndoRequestMessage>(OnUndoRequestReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingCanvasStateMessage>(OnCanvasStateReceived);
            _registeredBuffer.UnregisterMessageHandler<DrawingFinalMessage>(OnFinalReceived);
        }

        current.RegisterMessageHandler<DrawingSyncMessage>(OnCommandsReceived);
        current.RegisterMessageHandler<DrawingUndoRequestMessage>(OnUndoRequestReceived);
        current.RegisterMessageHandler<DrawingCanvasStateMessage>(OnCanvasStateReceived);
        current.RegisterMessageHandler<DrawingFinalMessage>(OnFinalReceived);
        _registeredBuffer = current;
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

    private static void OnCanvasStateReceived(DrawingCanvasStateMessage message, ulong senderId)
    {
        if (senderId != message.OwnerId)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Rejected undo canvas state from {senderId}; expected owner {message.OwnerId}.");
            return;
        }

        if (DrawingScreen.TryReceiveCanvasState(message))
        {
            return;
        }

        (ulong OwnerId, uint SessionId) key = (message.OwnerId, message.SessionId);
        if (!PendingCanvasStates.TryGetValue(key, out DrawingCanvasStateMessage? pending) || message.Epoch >= pending.Epoch)
        {
            PendingCanvasStates[key] = message;
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
