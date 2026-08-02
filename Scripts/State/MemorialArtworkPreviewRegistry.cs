using System;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.State;

internal static class MemorialArtworkPreviewRegistry
{
    private static readonly ConditionalWeakTable<CardModel, PreviewData> Previews = new();

    public static bool Register(CardModel card, MemorialArtworkData artwork)
    {
        Texture2D? texture = DecodeTexture(artwork.PngBase64);
        if (texture == null)
        {
            return false;
        }

        Previews.Remove(card);
        Previews.Add(card, new PreviewData(artwork, texture));
        return true;
    }

    public static bool TryGet(CardModel card, out MemorialArtworkData artwork, out Texture2D texture)
    {
        if (Previews.TryGetValue(card, out PreviewData? preview))
        {
            artwork = preview.Artwork;
            texture = preview.Texture;
            return true;
        }

        artwork = null!;
        texture = null!;
        return false;
    }

    public static void Propagate(AbstractModel source, AbstractModel clone)
    {
        if (source is not CardModel sourceCard ||
            clone is not CardModel clonedCard ||
            !Previews.TryGetValue(sourceCard, out PreviewData? preview))
        {
            return;
        }

        Previews.Remove(clonedCard);
        Previews.Add(clonedCard, preview);
    }

    private static Texture2D? DecodeTexture(string pngBase64)
    {
        try
        {
            Image image = new();
            return image.LoadPngFromBuffer(Convert.FromBase64String(pngBase64)) == Error.Ok
                ? ImageTexture.CreateFromImage(image)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record PreviewData(MemorialArtworkData Artwork, Texture2D Texture);
}
