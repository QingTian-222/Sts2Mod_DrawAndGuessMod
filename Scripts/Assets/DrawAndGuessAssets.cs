using Godot;

namespace DrawAndGuessMod.Scripts.Assets;

internal static class DrawAndGuessAssets
{
    public const string GalleryPortraitPath =
        "res://images/events/draw_and_guess_mod_event_vakuus_infinite_gallery.png";

    public static void Install()
    {
        if (!ResourceLoader.Exists(GalleryPortraitPath))
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Packed image asset not found: {GalleryPortraitPath}");
            return;
        }

        try
        {
            Texture2D? texture = ResourceLoader.Load<Texture2D>(
                GalleryPortraitPath,
                null,
                ResourceLoader.CacheMode.Reuse);
            if (texture == null)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] Packed image asset could not be loaded: {GalleryPortraitPath}");
                return;
            }

            Entry.Logger.Info($"[DrawAndGuessMod] Loaded packed image asset: {GalleryPortraitPath}");
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to load packed image asset '{GalleryPortraitPath}': {ex.Message}");
        }
    }
}
