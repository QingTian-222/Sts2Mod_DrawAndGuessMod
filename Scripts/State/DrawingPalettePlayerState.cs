using System.Collections.Generic;

namespace DrawAndGuessMod.Scripts.State;

public sealed class DrawingPalettePlayerState
{
    public List<uint> Colors { get; set; } = new();
}
