using System;
using System.Collections.Generic;
using System.IO;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Networking;

public sealed class DrawingChallengeTargetMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public string TargetCardId { get; set; } = "";
    public RunLocation LocationValue { get; set; }

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => true;
    public RunLocation Location => LocationValue;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(OwnerId);
        writer.WriteUInt(SessionId);
        writer.WriteString(TargetCardId);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        TargetCardId = reader.ReadString();
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawingChallengeTargetMessage owner={OwnerId} session={SessionId} target={TargetCardId}";
    }
}

public sealed class DrawingSyncMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public uint Epoch { get; set; }
    public List<DrawingCommand> Commands { get; set; } = new();
    public RunLocation LocationValue { get; set; }

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public RunLocation Location => LocationValue;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(OwnerId);
        writer.WriteUInt(SessionId);
        writer.WriteUInt(Epoch);
        writer.WriteList(Commands, 8);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        Epoch = reader.ReadUInt();
        Commands = reader.ReadList<DrawingCommand>(8);
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawingSyncMessage owner={OwnerId} session={SessionId} epoch={Epoch} commands={Commands.Count}";
    }
}

public sealed class DrawingUndoRequestMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public RunLocation LocationValue { get; set; }

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;
    public RunLocation Location => LocationValue;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(OwnerId);
        writer.WriteUInt(SessionId);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawingUndoRequestMessage owner={OwnerId} session={SessionId}";
    }
}

public sealed class DrawingRedoRequestMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public RunLocation LocationValue { get; set; }

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;
    public RunLocation Location => LocationValue;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(OwnerId);
        writer.WriteUInt(SessionId);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawingRedoRequestMessage owner={OwnerId} session={SessionId}";
    }
}

public sealed class DrawingCanvasStateMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    private const int MaxPngBytes = 2 * 1024 * 1024;
    private const int MaxWatermarks = 16;

    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public uint Epoch { get; set; }
    public uint StateSequence { get; set; }
    public DrawingCanvasMode CanvasMode { get; set; }
    public bool ResetPendingOperations { get; set; }
    public List<DrawingOperationWatermark> Watermarks { get; set; } = new();
    public byte[] PngBytes { get; set; } = [];
    public RunLocation LocationValue { get; set; }

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => true;
    public RunLocation Location => LocationValue;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(OwnerId);
        writer.WriteUInt(SessionId);
        writer.WriteUInt(Epoch);
        writer.WriteUInt(StateSequence);
        writer.WriteByte((byte)CanvasMode, 1);
        writer.WriteBool(ResetPendingOperations);
        int watermarkCount = Math.Min(Watermarks.Count, MaxWatermarks);
        writer.WriteByte((byte)watermarkCount, 5);
        for (int index = 0; index < watermarkCount; index++)
        {
            Watermarks[index].Serialize(writer);
        }
        writer.WriteInt(PngBytes.Length);
        writer.WriteBytes(PngBytes, PngBytes.Length);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        Epoch = reader.ReadUInt();
        StateSequence = reader.ReadUInt();
        CanvasMode = (DrawingCanvasMode)reader.ReadByte(1);
        ResetPendingOperations = reader.ReadBool();
        int watermarkCount = reader.ReadByte(5);
        if (watermarkCount > MaxWatermarks)
        {
            throw new InvalidDataException($"Invalid drawing operation watermark count: {watermarkCount}");
        }
        Watermarks = new List<DrawingOperationWatermark>(watermarkCount);
        for (int index = 0; index < watermarkCount; index++)
        {
            DrawingOperationWatermark watermark = new();
            watermark.Deserialize(reader);
            Watermarks.Add(watermark);
        }
        int pngLength = reader.ReadInt();
        if (pngLength < 0 || pngLength > MaxPngBytes)
        {
            throw new InvalidDataException($"Invalid authoritative canvas PNG size: {pngLength}");
        }
        PngBytes = new byte[pngLength];
        reader.ReadBytes(PngBytes, pngLength);
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawingCanvasStateMessage owner={OwnerId} session={SessionId} epoch={Epoch} sequence={StateSequence} mode={CanvasMode} reset={ResetPendingOperations} png={PngBytes.Length}";
    }
}

public sealed class DrawingOperationWatermark : IPacketSerializable
{
    public ulong SenderId { get; set; }
    public uint OperationId { get; set; }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(SenderId);
        writer.WriteUInt(OperationId);
    }

    public void Deserialize(PacketReader reader)
    {
        SenderId = reader.ReadULong();
        OperationId = reader.ReadUInt();
    }
}

public sealed class DrawingTimerSyncMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public int RemainingMilliseconds { get; set; }
    public RunLocation LocationValue { get; set; }

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Unreliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public RunLocation Location => LocationValue;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(OwnerId);
        writer.WriteUInt(SessionId);
        writer.WriteInt(RemainingMilliseconds);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        RemainingMilliseconds = reader.ReadInt();
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawingTimerSyncMessage owner={OwnerId} session={SessionId} remainingMs={RemainingMilliseconds}";
    }
}

public sealed class DrawingFinalMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    private const int MaxPngBytes = 2 * 1024 * 1024;

    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public bool Cancelled { get; set; }
    public bool SkipAddingToDeck { get; set; }
    public DrawingCanvasMode CanvasMode { get; set; }
    public List<string> CardIds { get; set; } = new();
    public byte[] PngBytes { get; set; } = [];
    public RunLocation LocationValue { get; set; }

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => true;
    public RunLocation Location => LocationValue;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(OwnerId);
        writer.WriteUInt(SessionId);
        writer.WriteBool(Cancelled);
        writer.WriteByte((byte)CanvasMode, 1);
        writer.WriteByte((byte)CardIds.Count, 3);
        for (int index = 0; index < CardIds.Count; index++)
        {
            writer.WriteString(CardIds[index]);
            // 复用旧协议保留的浮点槽位同步设置；负数哨兵不会与旧版置信度值冲突。
            writer.WriteFloat(index == 0 && SkipAddingToDeck ? -1f : 0f);
        }
        writer.WriteInt(PngBytes.Length);
        writer.WriteBytes(PngBytes, PngBytes.Length);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        Cancelled = reader.ReadBool();
        CanvasMode = (DrawingCanvasMode)reader.ReadByte(1);
        int cardCount = reader.ReadByte(3);
        CardIds = new List<string>(cardCount);
        for (int i = 0; i < cardCount; i++)
        {
            CardIds.Add(reader.ReadString());
            float compatibilityValue = reader.ReadFloat();
            if (i == 0)
            {
                SkipAddingToDeck = compatibilityValue < -0.5f;
            }
        }

        int pngLength = reader.ReadInt();
        if (pngLength < 0 || pngLength > MaxPngBytes)
        {
            throw new InvalidDataException($"Invalid collaborative drawing PNG size: {pngLength}");
        }
        PngBytes = new byte[pngLength];
        reader.ReadBytes(PngBytes, pngLength);
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawingFinalMessage owner={OwnerId} session={SessionId} cancelled={Cancelled} skipDeck={SkipAddingToDeck} mode={CanvasMode} cards={CardIds.Count} png={PngBytes.Length}";
    }
}
