using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.State;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Portrait), MethodType.Getter)]
internal static class CardPortraitPatch
{
    private static Texture2D? _blankTexture;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CardModel __instance, ref Texture2D __result)
    {
        if (__instance is Blank or DrawGuessBlank)
        {
            __result = GetBlankTexture();
            return;
        }

        if (ArtworkStore.TryGetTexture(__instance, out Texture2D texture))
        {
            __result = texture;
        }
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
