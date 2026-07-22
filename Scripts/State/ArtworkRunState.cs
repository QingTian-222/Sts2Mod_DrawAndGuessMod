namespace DrawAndGuessMod.Scripts.State;

public sealed class ArtworkRunState
{
    public List<CardArtworkData> Artworks { get; set; } = new();
}

public sealed class CardArtworkData
{
    public string CardId { get; set; } = string.Empty;
    public string PngBase64 { get; set; } = string.Empty;
}
