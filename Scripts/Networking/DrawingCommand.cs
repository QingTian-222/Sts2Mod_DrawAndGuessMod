using System;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace DrawAndGuessMod.Scripts.Networking;

public enum DrawingCommandKind
{
    Line,
    StrokeEnd,
    Fill,
    Stamp,
    Clear
}

public sealed class DrawingCommand : IPacketSerializable
{
    public DrawingCommandKind Kind { get; set; }
    public ushort X1 { get; set; }
    public ushort Y1 { get; set; }
    public ushort X2 { get; set; }
    public ushort Y2 { get; set; }
    public uint OperationId { get; set; }
    public uint ColorRgb { get; set; }
    public byte BrushSize { get; set; } = DrawingCanvas.DefaultBrushSize;
    public byte StampIndex { get; set; }
    public bool Erasing { get; set; }
    public bool CompletesOperation => Kind is DrawingCommandKind.StrokeEnd or DrawingCommandKind.Fill or DrawingCommandKind.Stamp or DrawingCommandKind.Clear;

    public static DrawingCommand Line(ushort x1, ushort y1, ushort x2, ushort y2, Color color, bool erasing, byte brushSize, uint operationId)
    {
        return new DrawingCommand
        {
            Kind = DrawingCommandKind.Line,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            OperationId = operationId,
            ColorRgb = PackRgb(color),
            Erasing = erasing,
            BrushSize = (byte)Math.Clamp((int)brushSize, DrawingCanvas.MinBrushSize, DrawingCanvas.MaxBrushSize)
        };
    }

    public static DrawingCommand StrokeEnd(uint operationId)
    {
        return new DrawingCommand { Kind = DrawingCommandKind.StrokeEnd, OperationId = operationId };
    }

    public static DrawingCommand Fill(ushort x, ushort y, Color color, uint operationId)
    {
        return new DrawingCommand { Kind = DrawingCommandKind.Fill, X1 = x, Y1 = y, ColorRgb = PackRgb(color), OperationId = operationId };
    }

    public static DrawingCommand Stamp(ushort x, ushort y, byte stampIndex, byte brushSize, uint operationId)
    {
        return new DrawingCommand
        {
            Kind = DrawingCommandKind.Stamp,
            X1 = x,
            Y1 = y,
            StampIndex = stampIndex,
            BrushSize = (byte)Math.Clamp((int)brushSize, DrawingCanvas.MinBrushSize, DrawingCanvas.MaxBrushSize),
            OperationId = operationId
        };
    }

    public static DrawingCommand Clear(uint operationId)
    {
        return new DrawingCommand { Kind = DrawingCommandKind.Clear, OperationId = operationId };
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteEnum(Kind);
        writer.WriteUInt(OperationId);
        switch (Kind)
        {
            case DrawingCommandKind.Line:
                writer.WriteUShort(X1);
                writer.WriteUShort(Y1);
                writer.WriteUShort(X2);
                writer.WriteUShort(Y2);
                writer.WriteUInt(ColorRgb, 24);
                writer.WriteByte(BrushSize, 6);
                writer.WriteBool(Erasing);
                break;
            case DrawingCommandKind.StrokeEnd:
                break;
            case DrawingCommandKind.Fill:
                writer.WriteUShort(X1);
                writer.WriteUShort(Y1);
                writer.WriteUInt(ColorRgb, 24);
                break;
            case DrawingCommandKind.Stamp:
                writer.WriteUShort(X1);
                writer.WriteUShort(Y1);
                writer.WriteByte(StampIndex, 3);
                writer.WriteByte(BrushSize, 6);
                break;
            case DrawingCommandKind.Clear:
                break;
        }
    }

    public void Deserialize(PacketReader reader)
    {
        Kind = reader.ReadEnum<DrawingCommandKind>();
        OperationId = reader.ReadUInt();
        switch (Kind)
        {
            case DrawingCommandKind.Line:
                X1 = reader.ReadUShort();
                Y1 = reader.ReadUShort();
                X2 = reader.ReadUShort();
                Y2 = reader.ReadUShort();
                ColorRgb = reader.ReadUInt(24);
                BrushSize = reader.ReadByte(6);
                Erasing = reader.ReadBool();
                break;
            case DrawingCommandKind.StrokeEnd:
                break;
            case DrawingCommandKind.Fill:
                X1 = reader.ReadUShort();
                Y1 = reader.ReadUShort();
                ColorRgb = reader.ReadUInt(24);
                break;
            case DrawingCommandKind.Stamp:
                X1 = reader.ReadUShort();
                Y1 = reader.ReadUShort();
                StampIndex = reader.ReadByte(3);
                BrushSize = reader.ReadByte(6);
                break;
            case DrawingCommandKind.Clear:
                break;
        }
    }

    public static uint PackRgb(Color color)
    {
        uint red = (uint)Math.Clamp(Mathf.RoundToInt(color.R * 255f), 0, 255);
        uint green = (uint)Math.Clamp(Mathf.RoundToInt(color.G * 255f), 0, 255);
        uint blue = (uint)Math.Clamp(Mathf.RoundToInt(color.B * 255f), 0, 255);
        return red << 16 | green << 8 | blue;
    }

    public static Color UnpackRgb(uint rgb)
    {
        return new Color(
            ((rgb >> 16) & 0xFFu) / 255f,
            ((rgb >> 8) & 0xFFu) / 255f,
            (rgb & 0xFFu) / 255f);
    }
}
