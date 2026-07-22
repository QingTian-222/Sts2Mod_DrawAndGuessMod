using Godot;

namespace DrawAndGuessMod.Scripts.Ui;

internal sealed record BrushPaletteEntry(string Name, Color Color);

internal static class BrushPalette
{
    public static readonly IReadOnlyList<BrushPaletteEntry> Entries =
    [
        new("黑色", new Color("1B1A18")),
        new("深灰", new Color("4A4A4A")),
        new("浅灰", new Color("B8B8B8")),
        new("白色", Colors.White),
        new("棕色", new Color("7A4B2A")),
        new("肤色", new Color("D2A66A")),
        new("深红", new Color("7F1D2D")),
        new("红色", new Color("D93A3A")),
        new("粉色", new Color("F27FA5")),
        new("橙色", new Color("E9842C")),
        new("金色", new Color("F2A900")),
        new("黄色", new Color("E8C83A")),
        new("米白", new Color("F4EEDC")),
        new("深绿", new Color("1F5A3A")),
        new("绿色", new Color("3C9B55")),
        new("黄绿", new Color("8CCF4D")),
        new("青色", new Color("38C5C9")),
        new("藏蓝", new Color("244A8D")),
        new("蓝色", new Color("367BD3")),
        new("天蓝", new Color("72B7E8")),
        new("靛色", new Color("4B3C96")),
        new("紫色", new Color("8C55C7")),
        new("洋红", new Color("C34FA3")),
        new("深紫", new Color("56316F"))
    ];
}
