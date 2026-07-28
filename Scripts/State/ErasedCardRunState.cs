using System.Collections.Generic;

namespace DrawAndGuessMod.Scripts.State;

public sealed class ErasedCardRunState
{
    public List<string> CardIds { get; set; } = new();
}
