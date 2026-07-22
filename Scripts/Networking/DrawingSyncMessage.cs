using System;
using System.Collections.Generic;
using System.IO;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Networking;

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

public sealed class DrawingCanvasStateMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    private const int MaxPngBytes = 2 * 1024 * 1024;

    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public uint Epoch { get; set; }
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
        writer.WriteInt(PngBytes.Length);
        writer.WriteBytes(PngBytes, PngBytes.Length);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        Epoch = reader.ReadUInt();
        int pngLength = reader.ReadInt();
        if (pngLength < 0 || pngLength > MaxPngBytes)
        {
            throw new InvalidDataException($"Invalid undo canvas PNG size: {pngLength}");
        }
        PngBytes = new byte[pngLength];
        reader.ReadBytes(PngBytes, pngLength);
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawingCanvasStateMessage owner={OwnerId} session={SessionId} epoch={Epoch} png={PngBytes.Length}";
    }
}

public sealed class DrawingFinalMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    private const int MaxPngBytes = 2 * 1024 * 1024;

    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public bool Cancelled { get; set; }
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
        writer.WriteByte((byte)CardIds.Count, 3);
        for (int index = 0; index < CardIds.Count; index++)
        {
            writer.WriteString(CardIds[index]);
            // 保留旧协议的浮点槽位，确保联机SL时仍能读取已经缓冲的消息；该值不再参与游戏逻辑。
            writer.WriteFloat(0f);
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
        int cardCount = reader.ReadByte(3);
        CardIds = new List<string>(cardCount);
        for (int i = 0; i < cardCount; i++)
        {
            CardIds.Add(reader.ReadString());
            _ = reader.ReadFloat();
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
        return $"DrawingFinalMessage owner={OwnerId} session={SessionId} cancelled={Cancelled} cards={CardIds.Count} png={PngBytes.Length}";
    }
}
