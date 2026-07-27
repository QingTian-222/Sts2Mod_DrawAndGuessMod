using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Networking;

/// <summary>
/// 你画我猜模式：绘画者（Player A）提交画作后广播，通知其余玩家开始猜测。
/// 附带最终画作 PNG，猜测端直接展示，无需重放绘画过程。
/// </summary>
public sealed class StartGuessingPacket : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    private const int MaxPngBytes = 2 * 1024 * 1024;
    private const int MaxGuessers = 8;

    /// <summary>绘画者（会话拥有者）的 NetId。</summary>
    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    public byte[] PngBytes { get; set; } = [];
    public byte GuessTimeoutSeconds { get; set; }
    /// <summary>需要提交猜测的玩家 NetId 列表（由绘画者权威给出）。</summary>
    public List<ulong> ExpectedGuesserIds { get; set; } = new();
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
        writer.WriteInt(PngBytes.Length);
        writer.WriteBytes(PngBytes, PngBytes.Length);
        writer.WriteByte(GuessTimeoutSeconds, 7);
        if (ExpectedGuesserIds.Count > MaxGuessers)
        {
            GD.PushWarning($"[DrawAndGuessMod] 猜测人数 {ExpectedGuesserIds.Count} 超过协议上限 {MaxGuessers}，超出部分将被截断。");
        }

        writer.WriteByte((byte)Math.Min(ExpectedGuesserIds.Count, MaxGuessers), 4);
        foreach (ulong guesserId in ExpectedGuesserIds.Take(MaxGuessers))
        {
            writer.WriteULong(guesserId);
        }
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        int pngLength = reader.ReadInt();
        if (pngLength < 0 || pngLength > MaxPngBytes)
        {
            throw new InvalidDataException($"Invalid start-guessing PNG size: {pngLength}");
        }
        PngBytes = new byte[pngLength];
        reader.ReadBytes(PngBytes, pngLength);
        GuessTimeoutSeconds = reader.ReadByte(7);
        int guesserCount = reader.ReadByte(4);
        if (guesserCount > MaxGuessers)
        {
            throw new InvalidDataException($"Invalid guesser count: {guesserCount}");
        }
        ExpectedGuesserIds = new List<ulong>(guesserCount);
        for (int i = 0; i < guesserCount; i++)
        {
            ExpectedGuesserIds.Add(reader.ReadULong());
        }
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"StartGuessingPacket owner={OwnerId} session={SessionId} png={PngBytes.Length} timeout={GuessTimeoutSeconds}s guessers={ExpectedGuesserIds.Count}";
    }
}

/// <summary>
/// 猜测端回传给绘画者的轻量结果。只带卡牌标识符（已在本端本地化卡名模糊检索确认），
/// 猜测者身份由传输层 senderId 提供，避免跨语言文本造成的同步问题。
/// </summary>
public sealed class GuessCardPacket : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    /// <summary>绘画者（会话拥有者）的 NetId。</summary>
    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    /// <summary>命中的卡牌 Id；空字符串表示该玩家弃权（仍计为已提交）。</summary>
    public string CardId { get; set; } = string.Empty;
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
        writer.WriteString(CardId);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        CardId = reader.ReadString();
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"GuessCardPacket owner={OwnerId} session={SessionId} card={(CardId.Length == 0 ? "<skip>" : CardId)}";
    }
}

/// <summary>
/// 绘画者聚合全部猜测后广播的最终裁定。所有端以此为唯一事实来源，
/// 之后各自走原生发卡管线（确定性一致）。
/// </summary>
public sealed class DrawGuessResultMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public ulong OwnerId { get; set; }
    public uint SessionId { get; set; }
    /// <summary>绘画者中途取消时为 true，各端直接放弃本次出牌后续流程。</summary>
    public bool Cancelled { get; set; }
    public string CardId { get; set; } = string.Empty;
    /// <summary>实际参与投票的猜测数量（含弃权），用于日志与 UI 展示。</summary>
    public byte TotalGuesses { get; set; }
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
        writer.WriteBool(Cancelled);
        writer.WriteString(CardId);
        writer.WriteByte((byte)Math.Min((int)TotalGuesses, 15), 4);
        writer.Write(LocationValue);
    }

    public void Deserialize(PacketReader reader)
    {
        OwnerId = reader.ReadULong();
        SessionId = reader.ReadUInt();
        Cancelled = reader.ReadBool();
        CardId = reader.ReadString();
        TotalGuesses = reader.ReadByte(4);
        LocationValue = reader.Read<RunLocation>();
    }

    public override string ToString()
    {
        return $"DrawGuessResultMessage owner={OwnerId} session={SessionId} cancelled={Cancelled} card={CardId} guesses={TotalGuesses}";
    }
}
