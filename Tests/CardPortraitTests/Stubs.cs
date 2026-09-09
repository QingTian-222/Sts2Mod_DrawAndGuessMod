// Test doubles for UI/storage; the real production patch and Harmony are used.
// BypassGetter deterministically simulates a call site that no longer invokes
// the hooked getter. It does NOT prove how RitsuLib changes JIT compilation.
using System.Runtime.CompilerServices;

namespace Godot
{
    public class Texture2D { }
    public class ImageTexture : Texture2D
    {
        public static ImageTexture CreateFromImage(Image image) => new();
    }
    public readonly record struct Color(string Value);
    public class Image
    {
        public enum Format { Rgba8 }
        public static Image CreateEmpty(int width, int height, bool mipmaps, Format format) => new();
        public void Fill(Color color) { }
        public void SetPixel(int x, int y, Color color) { }
    }
    public class TextureRect
    {
        private Texture2D? _texture;
        public int Assignments { get; private set; }
        public Texture2D? Texture
        {
            get => _texture;
            set { _texture = value; Assignments++; }
        }
    }
}

namespace MegaCrit.Sts2.Core.Entities.Cards
{
    public enum CardRarity { Common, Ancient }
}

namespace MegaCrit.Sts2.Core.Models
{
    public class CardModel
    {
        public Godot.Texture2D VanillaPortrait { get; } = new();
        public Godot.Texture2D? Artwork { get; set; }
        public Godot.Texture2D? PermanentArtwork { get; set; }
        public Godot.Texture2D? PreviewArtwork { get; set; }
        public bool HasPermanentArtwork { get; set; }
        public MegaCrit.Sts2.Core.Entities.Cards.CardRarity Rarity { get; set; }
        public Godot.Texture2D Portrait
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get => VanillaPortrait;
        }
    }
}

namespace DrawAndGuessMod.Scripts.Cards
{
    public class Blank : MegaCrit.Sts2.Core.Models.CardModel { }
}

namespace MegaCrit.Sts2.Core.Nodes.Cards
{
    public class NCard
    {
        public MegaCrit.Sts2.Core.Models.CardModel? Model { get; set; }
        public bool Ready { get; set; } = true;
        public bool BypassGetter { get; set; }
        public Dictionary<string, Godot.TextureRect> Nodes { get; } = new()
        {
            ["%Portrait"] = new(), ["%AncientPortrait"] = new()
        };
        public bool IsNodeReady() => Ready;
        public T? GetNodeOrNull<T>(string name) where T : class => Nodes.GetValueOrDefault(name) as T;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Reload()
        {
            if (Ready && Model != null) UpdatePortrait();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdatePortrait()
        {
            if (Model == null) return;
            string path = Model.Rarity == Entities.Cards.CardRarity.Ancient ? "%AncientPortrait" : "%Portrait";
            if (Nodes.TryGetValue(path, out var node))
                node.Texture = BypassGetter ? Model.VanillaPortrait : Model.Portrait;
        }
        public void UpdateVisuals()
        {
            if (Ready) UpdatePortrait();
        }
    }
}

namespace DrawAndGuessMod.Scripts.State
{
    using Godot;
    using MegaCrit.Sts2.Core.Models;
    public static class ArtworkStore
    {
        public static bool TryGetTexture(CardModel card, out Texture2D texture)
        {
            texture = card.Artwork!;
            return texture != null;
        }
    }
    public static class MemorialSketchbookStore
    {
        public static bool HasPermanentArtwork(CardModel card) => card.HasPermanentArtwork;
        public static bool TryGetPermanentTexture(CardModel card, out Texture2D texture)
        {
            texture = card.PermanentArtwork!;
            return texture != null;
        }
    }
    public static class MemorialArtworkPreviewRegistry
    {
        public static bool TryGet(CardModel card, out int unused, out Texture2D texture)
        {
            unused = 0;
            texture = card.PreviewArtwork!;
            return texture != null;
        }
    }
}
