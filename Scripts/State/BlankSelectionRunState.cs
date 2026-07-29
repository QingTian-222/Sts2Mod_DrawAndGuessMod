using System.Collections.Generic;

namespace DrawAndGuessMod.Scripts.State;

public sealed class BlankSelectionRunState
{
    public List<string> CardIds { get; set; } = new();
}
