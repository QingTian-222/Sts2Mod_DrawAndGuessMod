using Godot;
using DrawAndGuessMod.Scripts.Localization;

namespace DrawAndGuessMod.Scripts.Ui;

internal sealed record BrushPaletteEntry(string ChineseName, string EnglishName, Color Color)
{
    public string Name => ModText.Get(ChineseName, EnglishName);
}

internal static class BrushPalette
{
    public static readonly IReadOnlyList<BrushPaletteEntry> Entries =
    [
        new("黑色", "Black", new Color("1B1A18")),
        new("深灰", "Dark Gray", new Color("4A4A4A")),
        new("浅灰", "Light Gray", new Color("B8B8B8")),
        new("白色", "White", Colors.White),
        new("棕色", "Brown", new Color("7A4B2A")),
        new("肤色", "Skin Tone", new Color("D2A66A")),
        new("深红", "Dark Red", new Color("7F1D2D")),
        new("红色", "Red", new Color("D93A3A")),
        new("粉色", "Pink", new Color("F27FA5")),
        new("橙色", "Orange", new Color("E9842C")),
        new("金色", "Gold", new Color("F2A900")),
        new("黄色", "Yellow", new Color("E8C83A")),
        new("米白", "Ivory", new Color("F4EEDC")),
        new("深绿", "Dark Green", new Color("1F5A3A")),
        new("绿色", "Green", new Color("3C9B55")),
        new("黄绿", "Lime", new Color("8CCF4D")),
        new("青色", "Cyan", new Color("38C5C9")),
        new("藏蓝", "Navy", new Color("244A8D")),
        new("蓝色", "Blue", new Color("367BD3")),
        new("天蓝", "Sky Blue", new Color("72B7E8")),
        new("靛色", "Indigo", new Color("4B3C96")),
        new("紫色", "Purple", new Color("8C55C7")),
        new("洋红", "Magenta", new Color("C34FA3")),
        new("深紫", "Dark Purple", new Color("56316F"))
    ];
}
