using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.State;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Portrait), MethodType.Getter)]
internal static class CardPortraitPatch
{
    private static Texture2D? _blankTexture;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CardModel __instance, ref Texture2D __result)
    {
        if (TryGetOverrideTexture(__instance, out Texture2D texture))
        {
            __result = texture;
        }
    }

    internal static bool TryGetOverrideTexture(CardModel card, out Texture2D texture)
    {
        if (card is Blank)
        {
            texture = GetBlankTexture();
            return true;
        }

        if (MemorialArtworkPreviewRegistry.TryGet(
                card,
                out _,
                out Texture2D previewTexture))
        {
            texture = previewTexture;
            return true;
        }

        bool hasPermanentArtwork = MemorialSketchbookStore.HasPermanentArtwork(card);
        if (MemorialSketchbookStore.TryGetPermanentTexture(
                card,
                out Texture2D permanentTexture))
        {
            texture = permanentTexture;
            return true;
        }

        // A disabled permanent drawing must reveal the original card art rather
        // than falling through to this run's temporary drawing for the same ID.
        if (hasPermanentArtwork)
        {
            texture = null!;
            return false;
        }

        return ArtworkStore.TryGetTexture(card, out texture);
    }

    private static Texture2D GetBlankTexture()
    {
        if (_blankTexture != null)
        {
            return _blankTexture;
        }

        const int width = 500;
        const int height = 380;
        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        image.Fill(new Color("F4EEDC"));
        Color border = new("A89C7F");
        for (int x = 0; x < width; x++)
        {
            for (int borderWidth = 0; borderWidth < 7; borderWidth++)
            {
                image.SetPixel(x, borderWidth, border);
                image.SetPixel(x, height - 1 - borderWidth, border);
            }
        }
        for (int y = 0; y < height; y++)
        {
            for (int borderWidth = 0; borderWidth < 7; borderWidth++)
            {
                image.SetPixel(borderWidth, y, border);
                image.SetPixel(width - 1 - borderWidth, y, border);
            }
        }

        _blankTexture = ImageTexture.CreateFromImage(image);
        return _blankTexture;
    }
}

[HarmonyPatch(typeof(NCard), "Reload")]
internal static class NCardPortraitPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCard __instance)
    {
        CardModel? model = __instance.Model;
        if (model == null ||
            !CardPortraitPatch.TryGetOverrideTexture(
                model,
                out Texture2D texture))
        {
            return;
        }

        string portraitNode = model.Rarity == CardRarity.Ancient
            ? "%AncientPortrait"
            : "%Portrait";
        TextureRect? portrait =
            __instance.GetNodeOrNull<TextureRect>(portraitNode);
        if (portrait != null)
        {
            portrait.Texture = texture;
        }
    }
}
