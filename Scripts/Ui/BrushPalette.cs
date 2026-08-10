using Godot;
using DrawAndGuessMod.Scripts.Localization;

namespace DrawAndGuessMod.Scripts.Ui;

internal sealed record BrushPaletteEntry(string LocalizationKey, Color Color)
{
    public string Name => ModText.Get(LocalizationKey);
}

internal static class BrushPalette
{
    public static readonly IReadOnlyList<BrushPaletteEntry> Entries =
    [
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.BLACK", new Color("1B1A18")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.DARK_GRAY", new Color("4A4A4A")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.LIGHT_GRAY", new Color("B8B8B8")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.WHITE", Colors.White),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.BROWN", new Color("7A4B2A")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.SKIN_TONE", new Color("D2A66A")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.DARK_RED", new Color("7F1D2D")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.RED", new Color("D93A3A")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.PINK", new Color("F27FA5")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.ORANGE", new Color("E9842C")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.GOLD", new Color("F2A900")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.YELLOW", new Color("E8C83A")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.IVORY", new Color("F4EEDC")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.DARK_GREEN", new Color("1F5A3A")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.GREEN", new Color("3C9B55")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.LIME", new Color("8CCF4D")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.CYAN", new Color("38C5C9")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.NAVY", new Color("244A8D")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.BLUE", new Color("367BD3")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.SKY_BLUE", new Color("72B7E8")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.INDIGO", new Color("4B3C96")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.PURPLE", new Color("8C55C7")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.MAGENTA", new Color("C34FA3")),
        new("DRAW_AND_GUESS_MOD.BRUSH_PALETTE.DARK_PURPLE", new Color("56316F"))
    ];
}
