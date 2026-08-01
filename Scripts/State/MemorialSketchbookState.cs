using System.Collections.Generic;

namespace DrawAndGuessMod.Scripts.State;

public sealed class MemorialSketchbookRunState
{
    public int NextArtworkOrdinal { get; set; }
    public List<MemorialArtworkData> Artworks { get; set; } = new();
}

public sealed class MemorialArtworkData
{
    public string ArtworkId { get; set; } = string.Empty;
    public string CardId { get; set; } = string.Empty;
    public ulong DrawerNetId { get; set; }
    public uint SessionId { get; set; }
    public string PngBase64 { get; set; } = string.Empty;
}

public sealed class MemorialSketchbookProfileData
{
    public List<MemorialRunArchive> Runs { get; set; } = new();
    public List<PermanentCardArtworkData> PermanentArtworks { get; set; } = new();
}

public sealed class MemorialRunArchive
{
    public string RunKey { get; set; } = string.Empty;
    public string Seed { get; set; } = string.Empty;
    public long StartTime { get; set; }
    public List<MemorialArtworkData> Artworks { get; set; } = new();
}

public sealed class PermanentCardArtworkData
{
    public string CardId { get; set; } = string.Empty;
    public string ArtworkId { get; set; } = string.Empty;
    public string PngBase64 { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
