using System.Collections.Generic;

namespace DrawAndGuessMod.Scripts.State;

public sealed class DrawingPalettePlayerState
{
    public int ColorEncodingVersion { get; set; }
    public List<uint> Colors { get; set; } = new();
}
