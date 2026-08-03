namespace DrawAndGuessMod.Scripts.State;

internal sealed class BlankReplaySequence
{
    private uint _nextOrdinal;

    public uint NextSessionId(uint combatCardIndex)
    {
        _nextOrdinal++;
        if (_nextOrdinal == 0u)
        {
            _nextOrdinal = 1u;
        }

        return ComposeSessionId(combatCardIndex, _nextOrdinal);
    }

    internal static uint ComposeSessionId(uint combatCardIndex, uint ordinal)
    {
        // Reserve a namespace for Blank sessions, then combine the synchronized
        // combat-card index with this card's play ordinal. This keeps Replay
        // sessions distinct without depending on local action-queue ids.
        return 0xB0000000u |
               (((combatCardIndex + 1u) & 0xffffu) << 12) |
               (ordinal & 0xfffu);
    }
}
