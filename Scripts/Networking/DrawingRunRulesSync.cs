using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.State;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Networking;

public sealed class DrawingRunRulesMessage : INetMessage, IPacketSerializable
{
    public InitialBlankMode InitialBlankMode { get; set; }
    public int DrawingTimeLimitSeconds { get; set; }
    public bool ExcludePreviouslySelectedCards { get; set; }
    public bool BlankGeneratedCardSkipsDeck { get; set; }
    public DrawingCardRestriction CardRestriction { get; set; }
    public List<ulong> InitialBlankRecipients { get; set; } = new();

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte((byte)InitialBlankMode, 2);
        writer.WriteByte((byte)DrawingTimeLimitSeconds);
        writer.WriteBool(ExcludePreviouslySelectedCards);
        writer.WriteBool(BlankGeneratedCardSkipsDeck);
        writer.WriteByte((byte)CardRestriction, 2);
        writer.WriteByte((byte)Math.Min(InitialBlankRecipients.Count, 8), 4);
        foreach (ulong recipientId in InitialBlankRecipients.GetRange(0, Math.Min(InitialBlankRecipients.Count, 8)))
        {
            writer.WriteULong(recipientId);
        }
    }

    public void Deserialize(PacketReader reader)
    {
        InitialBlankMode = (InitialBlankMode)reader.ReadByte(2);
        DrawingTimeLimitSeconds = reader.ReadByte();
        ExcludePreviouslySelectedCards = reader.ReadBool();
        BlankGeneratedCardSkipsDeck = reader.ReadBool();
        CardRestriction = (DrawingCardRestriction)reader.ReadByte(2);
        int recipientCount = reader.ReadByte(4);
        InitialBlankRecipients = new List<ulong>(recipientCount);
        for (int index = 0; index < recipientCount; index++)
        {
            InitialBlankRecipients.Add(reader.ReadULong());
        }
    }
}

internal static class DrawingRunRulesSync
{
    private static INetGameService? _registeredService;

    public static void Attach(INetGameService netService)
    {
        if (ReferenceEquals(_registeredService, netService))
        {
            return;
        }

        Detach();
        netService.RegisterMessageHandler<DrawingRunRulesMessage>(OnMessageReceived);
        _registeredService = netService;
    }

    public static void Publish(DrawingRunRuleState state)
    {
        if (!DrawingNetSync.IsMultiplayer)
        {
            return;
        }

        INetGameService netService = RunManager.Instance.NetService;
        Attach(netService);
        netService.SendMessage(new DrawingRunRulesMessage
        {
            InitialBlankMode = (InitialBlankMode)state.InitialBlankMode,
            DrawingTimeLimitSeconds = state.DrawingTimeLimitSeconds,
            ExcludePreviouslySelectedCards = state.ExcludePreviouslySelectedCards,
            BlankGeneratedCardSkipsDeck = state.BlankGeneratedCardSkipsDeck,
            CardRestriction = (DrawingCardRestriction)state.CardRestriction,
            InitialBlankRecipients = new List<ulong>(state.InitialBlankRecipients)
        });
    }

    private static void Detach()
    {
        if (_registeredService == null)
        {
            return;
        }

        _registeredService.UnregisterMessageHandler<DrawingRunRulesMessage>(OnMessageReceived);
        _registeredService = null;
    }

    private static void OnMessageReceived(DrawingRunRulesMessage message, ulong senderId)
    {
        if (_registeredService == null || senderId == _registeredService.NetId)
        {
            return;
        }
        if (senderId != DrawingNetSync.HostNetId)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Rejected run drawing rules from {senderId}; expected host {DrawingNetSync.HostNetId}.");
            return;
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Ignored run drawing rules because no active run was available.");
            return;
        }

        DrawingRunRules.ApplySyncedSettings(runState, new DrawingRunRuleState
        {
            Configured = true,
            InitialBlankMode = (int)message.InitialBlankMode,
            DrawingTimeLimitSeconds = message.DrawingTimeLimitSeconds,
            ExcludePreviouslySelectedCards = message.ExcludePreviouslySelectedCards,
            BlankGeneratedCardSkipsDeck = message.BlankGeneratedCardSkipsDeck,
            CardRestriction = (int)message.CardRestriction,
            InitialBlankRecipients = new List<ulong>(message.InitialBlankRecipients)
        });
    }
}
